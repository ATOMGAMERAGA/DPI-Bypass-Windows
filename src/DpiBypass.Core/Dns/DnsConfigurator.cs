using System.Net.NetworkInformation;
using System.Text.Json;
using DpiBypass.Core.Interop;

namespace DpiBypass.Core.Dns;

public enum DnsMode
{
    /// <summary>Leave whatever the network handed out alone.</summary>
    SystemDefault = 0,

    /// <summary>Point Windows at our loopback DoH proxy. Encrypted, cached, unpoisonable.</summary>
    EncryptedLoopback = 1,

    /// <summary>Plain UDP to Cloudflare/Google/Quad9. Used when port 53 is unavailable.</summary>
    PublicResolvers = 2,
}

/// <summary>One adapter's DNS configuration as we found it, so it can be put back.</summary>
public sealed record AdapterDnsSnapshot(
    string Id,
    string Name,
    int InterfaceIndexV4,
    int InterfaceIndexV6,
    string[] OriginalV4,
    string[] OriginalV6);

/// <summary>
/// Switches the machine's resolvers and - more importantly - puts them back.
/// </summary>
/// <remarks>
/// The snapshot is written to ProgramData before anything changes, so if the
/// process is killed the next launch (or the installer's uninstall step) can still
/// restore the user's original DNS instead of leaving them on a dead loopback
/// address.
/// </remarks>
public sealed class DnsConfigurator
{
    public static readonly string[] PublicV4 = ["1.1.1.1", "1.0.0.1", "8.8.8.8", "9.9.9.9"];
    public static readonly string[] PublicV6 = ["2606:4700:4700::1111", "2001:4860:4860::8888", "2620:fe::fe"];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _snapshotPath;
    private readonly Action<string>? _log;

    public DnsConfigurator(string stateDirectory, Action<string>? log = null)
    {
        _snapshotPath = Path.Combine(stateDirectory, "dns-snapshot.json");
        _log = log;
    }

    public DnsMode CurrentMode { get; private set; } = DnsMode.SystemDefault;

    /// <summary>True when a snapshot is on disk, meaning DNS is (or was) redirected.</summary>
    public bool HasPendingRestore => File.Exists(_snapshotPath);

    /// <summary>
    /// The resolvers the machine was using before this app redirected it.
    /// </summary>
    /// <remarks>
    /// Handed to the proxy's plain fallback so that a network where encrypted DNS is
    /// blocked outright still resolves names through whatever it was resolving them
    /// with before. Empty when nothing has been redirected, or when the adapters were
    /// on DHCP-assigned servers we never recorded.
    /// </remarks>
    public IReadOnlyList<string> OriginalServers()
    {
        var snapshot = LoadSnapshot();
        if (snapshot is null)
        {
            return [];
        }

        return
        [
            .. snapshot
                .SelectMany(adapter => adapter.OriginalV4.Concat(adapter.OriginalV6))
                .Where(server => !string.IsNullOrWhiteSpace(server))
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    public async Task<bool> ApplyAsync(DnsMode mode, bool loopbackHasIPv6, CancellationToken cancellationToken = default)
    {
        if (mode == DnsMode.SystemDefault)
        {
            await RestoreAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        var adapters = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
        if (adapters.Count == 0)
        {
            _log?.Invoke("No active adapter found; leaving DNS untouched.");
            return false;
        }

        var snapshotWritten = false;
        if (!HasPendingRestore)
        {
            SaveSnapshot(adapters);
            snapshotWritten = true;
        }

        var v4 = mode == DnsMode.EncryptedLoopback ? ["127.0.0.1"] : PublicV4;
        var v6 = mode == DnsMode.EncryptedLoopback
            ? (loopbackHasIPv6 ? ["::1"] : PublicV6)
            : PublicV6;

        var steps = new List<DnsStep>();
        foreach (var adapter in adapters)
        {
            steps.Add(DnsStep.Set(adapter.InterfaceIndexV4, v4, counts: true));

            if (adapter.InterfaceIndexV6 > 0)
            {
                steps.Add(DnsStep.Set(adapter.InterfaceIndexV6, v6, counts: false));
            }
        }

        var applied = await RunPlanAsync(steps, cancellationToken).ConfigureAwait(false);

        if (applied == 0)
        {
            _log?.Invoke("No adapter accepted the new DNS servers; the previous configuration is still in place.");

            // A snapshot left behind here would offer to "restore" a change that never
            // happened, and the caller would report encrypted DNS as active.
            if (snapshotWritten)
            {
                DeleteSnapshot();
            }

            return false;
        }

        CurrentMode = mode;
        _log?.Invoke($"DNS set to {(mode == DnsMode.EncryptedLoopback ? "encrypted loopback proxy" : "public resolvers")} on {applied} adapter(s).");
        return true;
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = LoadSnapshot();
        if (snapshot is null)
        {
            CurrentMode = DnsMode.SystemDefault;
            return;
        }

        // The interface index is per adapter rather than per family and
        // -ResetServerAddresses takes no family, so a reset clears both families at
        // once. Every reset therefore has to happen before the explicit servers go
        // back on, otherwise the v6 reset undoes the v4 servers just restored - which
        // is why the plan is built in two passes rather than one.
        var steps = new List<DnsStep>();

        foreach (var adapter in snapshot)
        {
            if (adapter.OriginalV4.Length == 0)
            {
                steps.Add(DnsStep.Reset(adapter.InterfaceIndexV4));
            }

            if (adapter.InterfaceIndexV6 > 0 && adapter.OriginalV6.Length == 0)
            {
                steps.Add(DnsStep.Reset(adapter.InterfaceIndexV6));
            }
        }

        foreach (var adapter in snapshot)
        {
            if (adapter.OriginalV4.Length > 0)
            {
                steps.Add(DnsStep.Set(adapter.InterfaceIndexV4, adapter.OriginalV4, counts: true));
            }

            if (adapter.InterfaceIndexV6 > 0 && adapter.OriginalV6.Length > 0)
            {
                steps.Add(DnsStep.Set(adapter.InterfaceIndexV6, adapter.OriginalV6, counts: false));
            }
        }

        await RunPlanAsync(steps, cancellationToken).ConfigureAwait(false);
        DeleteSnapshot();
        CurrentMode = DnsMode.SystemDefault;
        _log?.Invoke("Original DNS configuration restored.");
    }

    /// <summary>Every adapter that currently carries traffic, with both address family indexes.</summary>
    public async Task<IReadOnlyList<AdapterDnsSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var result = new List<AdapterDnsSnapshot>();

        // The managed API gives us the reliable "is this adapter actually usable"
        // answer; PowerShell fills in the per family interface indexes.
        var live = NetworkInterface.GetAllNetworkInterfaces()
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .ToList();

        // AddressFamily comes back as "IPv4"/"IPv6" on some builds, "InterNetwork"/
        // "InterNetworkV6" or the bare enum numbers 2/23 on others, so it is normalised
        // here instead of being spelled out again on the parsing side.
        var script = """
            $out = @()
            foreach ($a in Get-DnsClientServerAddress -ErrorAction SilentlyContinue) {
              $raw = [string]$a.AddressFamily
              if ($raw -match 'v6|23') { $family = 'v6' } else { $family = 'v4' }
              $out += [pscustomobject]@{
                Index  = $a.InterfaceIndex
                Alias  = $a.InterfaceAlias
                Family = $family
                Servers = @($a.ServerAddresses)
              }
            }
            $out | ConvertTo-Json -Depth 4 -Compress
            """;

        var probe = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        var rows = ParseRows(probe.StandardOutput);

        foreach (var nic in live)
        {
            var alias = nic.Name;
            var v4 = rows.FirstOrDefault(r => r.Alias == alias && IsV4Family(r.Family));
            var v6 = rows.FirstOrDefault(r => r.Alias == alias && IsV6Family(r.Family));

            var indexV4 = v4.Index;
            if (indexV4 == 0)
            {
                try
                {
                    indexV4 = nic.GetIPProperties().GetIPv4Properties()?.Index ?? 0;
                }
                catch (Exception)
                {
                    indexV4 = 0;
                }
            }

            if (indexV4 == 0)
            {
                continue;
            }

            result.Add(new AdapterDnsSnapshot(
                nic.Id,
                alias,
                indexV4,
                v6.Index,
                FilterOurOwn(v4.Servers),
                FilterOurOwn(v6.Servers)));
        }

        return result;
    }

    /// <summary>
    /// Never record our own loopback address as "the original" - that is how users
    /// end up permanently pointed at a proxy that is no longer running.
    /// </summary>
    private static string[] FilterOurOwn(string[]? servers) => servers is null
        ? []
        : [.. servers.Where(s => s is not ("127.0.0.1" or "::1") && !string.IsNullOrWhiteSpace(s))];

    /// <summary>
    /// Every spelling Windows has used for AddressFamily is accepted, in case the
    /// snapshot script ever runs against a build whose value it did not normalise.
    /// </summary>
    private static bool IsV6Family(string family)
    {
        var value = family.Trim();
        return value.Equals("v6", StringComparison.OrdinalIgnoreCase)
            || value.Equals("IPv6", StringComparison.OrdinalIgnoreCase)
            || value.Equals("InterNetworkV6", StringComparison.OrdinalIgnoreCase)
            || value == "23";
    }

    /// <summary>
    /// A positive match on top of "not v6", so an unrecognised spelling is dropped
    /// rather than silently filed as the IPv4 row.
    /// </summary>
    private static bool IsV4Family(string family)
    {
        var value = family.Trim();
        return !IsV6Family(value)
            && (value.Equals("v4", StringComparison.OrdinalIgnoreCase)
                || value.Equals("IPv4", StringComparison.OrdinalIgnoreCase)
                || value.Equals("InterNetwork", StringComparison.OrdinalIgnoreCase)
                || value == "2");
    }

    private static List<Row> ParseRows(string json)
    {
        var rows = new List<Row>();
        var trimmed = json.Trim();
        if (trimmed.Length == 0)
        {
            return rows;
        }

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            var root = document.RootElement;
            var items = root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : [root];

            foreach (var item in items)
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var servers = new List<string>();
                if (item.TryGetProperty("Servers", out var serverNode))
                {
                    if (serverNode.ValueKind == JsonValueKind.Array)
                    {
                        servers.AddRange(serverNode.EnumerateArray()
                            .Where(s => s.ValueKind == JsonValueKind.String)
                            .Select(s => s.GetString()!));
                    }
                    else if (serverNode.ValueKind == JsonValueKind.String)
                    {
                        servers.Add(serverNode.GetString()!);
                    }
                }

                rows.Add(new Row(
                    item.TryGetProperty("Index", out var index) && index.TryGetInt32(out var value) ? value : 0,
                    item.TryGetProperty("Alias", out var alias) ? alias.GetString() ?? string.Empty : string.Empty,
                    item.TryGetProperty("Family", out var family) ? family.GetString() ?? string.Empty : string.Empty,
                    [.. servers]));
            }
        }
        catch (JsonException)
        {
            return rows;
        }

        return rows;
    }

    /// <summary>One interface's worth of work: either explicit servers, or a reset.</summary>
    internal readonly record struct DnsStep(int Index, string[]? Servers, bool Counts)
    {
        public static DnsStep Set(int index, string[] servers, bool counts) => new(index, servers, counts);

        public static DnsStep Reset(int index) => new(index, null, Counts: false);
    }

    /// <summary>
    /// The whole plan, plus the cache flush, in one PowerShell process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This used to be one <c>powershell.exe</c> per interface per address family,
    /// plus one to enumerate and one to flush. Starting PowerShell is not cheap -
    /// half a second on a warm machine, several on a cold one, and every one of them
    /// is a process the real time scanner opens - so a laptop with a Wi-Fi adapter, an
    /// Ethernet port and a couple of virtual ones spent the better part of a minute
    /// in here. That minute was spent with the machine's resolvers already pointing
    /// at a proxy that was not finished starting, which is exactly what "it broke my
    /// internet and then the window finally appeared" describes.
    /// </para>
    /// <para>
    /// The plan is handed over as JSON in an environment variable rather than
    /// interpolated into the script, so nothing derived from an adapter name or a
    /// server address is ever parsed as PowerShell.
    /// </para>
    /// </remarks>
    private static async Task<int> RunPlanAsync(IReadOnlyList<DnsStep> steps, CancellationToken cancellationToken)
    {
        if (steps.Count == 0)
        {
            await FlushCacheAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var plan = JsonSerializer.Serialize(steps.Select(step => new
        {
            step.Index,
            Reset = step.Servers is null,
            Servers = step.Servers ?? [],
            step.Counts,
        }));

        const string script = """
            $applied = 0
            foreach ($step in @($env:DPI_BYPASS_DNS_PLAN | ConvertFrom-Json)) {
              try {
                if ($step.Reset) {
                  Set-DnsClientServerAddress -InterfaceIndex $step.Index -ResetServerAddresses -ErrorAction Stop
                } else {
                  Set-DnsClientServerAddress -InterfaceIndex $step.Index -ServerAddresses @($step.Servers) -ErrorAction Stop
                }
                if ($step.Counts) { $applied++ }
              } catch { }
            }
            Clear-DnsClientCache -ErrorAction SilentlyContinue
            Write-Output "APPLIED=$applied"
            """;

        // Generous, because it now covers every interface rather than one: a machine
        // with a stack of virtual adapters still has to finish inside it.
        var result = await ProcessRunner.PowerShellWithEnvironmentAsync(
            script,
            new Dictionary<string, string?> { ["DPI_BYPASS_DNS_PLAN"] = plan },
            TimeSpan.FromSeconds(45),
            cancellationToken).ConfigureAwait(false);

        return ReadApplied(result.StandardOutput);
    }

    /// <summary>Reads the count the script reported, ignoring anything else it printed.</summary>
    internal static int ReadApplied(string output)
    {
        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("APPLIED=", StringComparison.Ordinal)
                && int.TryParse(trimmed["APPLIED=".Length..], out var value))
            {
                return value;
            }
        }

        return 0;
    }

    public static Task FlushCacheAsync(CancellationToken cancellationToken = default)
        => ProcessRunner.PowerShellAsync("Clear-DnsClientCache -ErrorAction SilentlyContinue", TimeSpan.FromSeconds(15), cancellationToken);

    private void SaveSnapshot(IReadOnlyList<AdapterDnsSnapshot> adapters)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
            File.WriteAllText(_snapshotPath, JsonSerializer.Serialize(adapters, JsonOptions));
        }
        catch (IOException ex)
        {
            _log?.Invoke($"Could not persist DNS snapshot: {ex.Message}");
        }
    }

    private IReadOnlyList<AdapterDnsSnapshot>? LoadSnapshot()
    {
        if (!File.Exists(_snapshotPath))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<List<AdapterDnsSnapshot>>(File.ReadAllText(_snapshotPath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void DeleteSnapshot()
    {
        try
        {
            if (File.Exists(_snapshotPath))
            {
                File.Delete(_snapshotPath);
            }
        }
        catch (IOException)
        {
            // Left behind; the next restore will simply be a no-op.
        }
    }

    private readonly record struct Row(int Index, string Alias, string Family, string[] Servers);
}
