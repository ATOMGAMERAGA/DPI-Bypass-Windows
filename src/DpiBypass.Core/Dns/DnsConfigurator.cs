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

        if (!HasPendingRestore)
        {
            SaveSnapshot(adapters);
        }

        var v4 = mode == DnsMode.EncryptedLoopback ? ["127.0.0.1"] : PublicV4;
        var v6 = mode == DnsMode.EncryptedLoopback
            ? (loopbackHasIPv6 ? ["::1"] : PublicV6)
            : PublicV6;

        var applied = 0;
        foreach (var adapter in adapters)
        {
            if (await SetServersAsync(adapter.InterfaceIndexV4, v4, cancellationToken).ConfigureAwait(false))
            {
                applied++;
            }

            if (adapter.InterfaceIndexV6 > 0)
            {
                await SetServersAsync(adapter.InterfaceIndexV6, v6, cancellationToken).ConfigureAwait(false);
            }
        }

        await FlushCacheAsync(cancellationToken).ConfigureAwait(false);
        CurrentMode = mode;
        _log?.Invoke($"DNS set to {(mode == DnsMode.EncryptedLoopback ? "encrypted loopback proxy" : "public resolvers")} on {applied} adapter(s).");
        return applied > 0;
    }

    public async Task RestoreAsync(CancellationToken cancellationToken = default)
    {
        var snapshot = LoadSnapshot();
        if (snapshot is null)
        {
            CurrentMode = DnsMode.SystemDefault;
            return;
        }

        foreach (var adapter in snapshot)
        {
            if (adapter.OriginalV4.Length == 0)
            {
                await ResetServersAsync(adapter.InterfaceIndexV4, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await SetServersAsync(adapter.InterfaceIndexV4, adapter.OriginalV4, cancellationToken).ConfigureAwait(false);
            }

            if (adapter.InterfaceIndexV6 > 0)
            {
                if (adapter.OriginalV6.Length == 0)
                {
                    await ResetServersAsync(adapter.InterfaceIndexV6, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await SetServersAsync(adapter.InterfaceIndexV6, adapter.OriginalV6, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        await FlushCacheAsync(cancellationToken).ConfigureAwait(false);
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

        var script = """
            $out = @()
            foreach ($a in Get-DnsClientServerAddress -ErrorAction SilentlyContinue) {
              $out += [pscustomobject]@{
                Index  = $a.InterfaceIndex
                Alias  = $a.InterfaceAlias
                Family = $a.AddressFamily.ToString()
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
            var v4 = rows.FirstOrDefault(r => r.Alias == alias && r.Family.Contains("InterNetwork", StringComparison.OrdinalIgnoreCase) && !r.Family.Contains("V6", StringComparison.OrdinalIgnoreCase));
            var v6 = rows.FirstOrDefault(r => r.Alias == alias && r.Family.Contains("V6", StringComparison.OrdinalIgnoreCase));

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

    private static async Task<bool> SetServersAsync(int interfaceIndex, string[] servers, CancellationToken cancellationToken)
    {
        var list = string.Join(',', servers.Select(s => $"'{s}'"));
        var script = $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ServerAddresses ({list}) -ErrorAction Stop";
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return result.Success;
    }

    private static async Task<bool> ResetServersAsync(int interfaceIndex, CancellationToken cancellationToken)
    {
        var script = $"Set-DnsClientServerAddress -InterfaceIndex {interfaceIndex} -ResetServerAddresses -ErrorAction SilentlyContinue";
        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false);
        return result.Success;
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
