using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
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

        // A snapshot is a hard precondition, not a best-effort courtesy. Redirecting
        // Windows to a process-local DNS listener without first proving that the old
        // values are durable is how a crash turns into a machine with no internet.
        //
        // A pending snapshot may belong to an earlier run or another network. Keep its
        // original values, refresh interface indexes and add newly active adapters
        // before touching any of them. Without the merge, starting after a Wi-Fi/
        // Ethernet switch redirects the new adapter but later restores only the old
        // one, leaving the current connection permanently on 127.0.0.1.
        IReadOnlyList<AdapterDnsSnapshot>? previousSnapshot = null;
        if (HasPendingRestore)
        {
            previousSnapshot = LoadSnapshot();
            if (previousSnapshot is null)
            {
                _log?.Invoke("DNS snapshot is unreadable; recovering loopback DNS before continuing.");
                if (!await RecoverOrphanedLoopbackAsync(cancellationToken).ConfigureAwait(false))
                {
                    _log?.Invoke("Unreadable DNS snapshot could not be recovered; leaving DNS untouched.");
                    return false;
                }

                PreserveUnreadableSnapshot();
                previousSnapshot = [];
                adapters = await EnumerateAsync(cancellationToken).ConfigureAwait(false);
                if (adapters.Count == 0)
                {
                    _log?.Invoke("No active adapter found after DNS recovery; leaving DNS untouched.");
                    return false;
                }
            }
        }

        var snapshot = MergeSnapshots(previousSnapshot ?? [], adapters);
        PersistSnapshot(snapshot);

        var v4 = mode == DnsMode.EncryptedLoopback ? ["127.0.0.1"] : PublicV4;
        var v6 = mode == DnsMode.EncryptedLoopback
            ? (loopbackHasIPv6 ? ["::1"] : PublicV6)
            : PublicV6;

        var writes = new List<DnsWrite>(adapters.Count * 2);
        var v4Writes = new List<int>(adapters.Count);

        foreach (var adapter in adapters)
        {
            v4Writes.Add(writes.Count);
            writes.Add(new DnsWrite(adapter.InterfaceIndexV4, v4));

            if (adapter.InterfaceIndexV6 > 0)
            {
                writes.Add(new DnsWrite(adapter.InterfaceIndexV6, v6));
            }
        }

        // One PowerShell process for the whole machine, cache flush included, rather
        // than one per adapter and family. See ApplyWritesAsync.
        var outcome = await ApplyWritesAsync(writes, flushCache: true, cancellationToken).ConfigureAwait(false);
        var applied = v4Writes.Count(index => outcome[index]);

        if (applied == 0)
        {
            _log?.Invoke("No adapter accepted the new DNS servers; the previous configuration is still in place.");

            // Put the snapshot file back exactly as it was. A brand-new one would
            // claim a change happened when none did; an older one still represents a
            // redirect from the earlier run and must remain available for recovery.
            if (previousSnapshot is null or { Count: 0 })
            {
                DeleteSnapshot();
            }
            else
            {
                PersistSnapshot(previousSnapshot);
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
            if (HasPendingRestore)
            {
                _log?.Invoke("DNS snapshot is unreadable; resetting adapters that still point only at the local proxy.");
                if (!await RecoverOrphanedLoopbackAsync(cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidOperationException(
                        "DNS kurtarma dosyası okunamadı ve yerel DNS yönlendirmesi geri alınamadı.");
                }

                PreserveUnreadableSnapshot();
                await FlushCacheAsync(cancellationToken).ConfigureAwait(false);
                _log?.Invoke("Orphaned loopback DNS configuration reset to the network defaults.");
            }

            CurrentMode = DnsMode.SystemDefault;
            return;
        }

        var remaining = new List<AdapterDnsSnapshot>();
        var present = CurrentInterfaceIndexes();
        var unrepaired = 0;

        // Built first and run as one batch, for the same reason the apply path is one
        // batch: a per-adapter PowerShell process is most of a minute on a machine with
        // several adapters, and this one runs while the user is waiting to shut down.
        var writes = new List<DnsWrite>(snapshot.Count * 3);
        var perAdapter = new List<(AdapterDnsSnapshot Adapter, List<int> Ordinals, bool Repairable)>();

        foreach (var adapter in snapshot)
        {
            // Hardware that is not here cannot be repaired and does not need to be: an
            // interface Windows no longer has is not resolving through anything, least
            // of all through our loopback proxy. Its row is kept, because the dongle
            // may be plugged back in, but it is not counted as a failure - a snapshot
            // naming one absent adapter would otherwise fail every restore for as long
            // as it stayed absent, and a failed restore is what stops the engine from
            // starting at all.
            if (present.Count > 0
                && !present.Contains(adapter.InterfaceIndexV4)
                && !present.Contains(adapter.InterfaceIndexV6))
            {
                _log?.Invoke($"Adapter '{adapter.Name}' is not on this machine; its recovery data is kept for later.");
                remaining.Add(adapter);
                continue;
            }

            // The interface index is per adapter rather than per family and
            // -ResetServerAddresses takes no family, so a reset clears both families at
            // once. Every reset therefore has to happen before the explicit servers go
            // back on, otherwise the v6 reset undoes the v4 servers just restored. The
            // batch preserves this order because it is the order they are queued in.
            var ordinals = new List<int>(3);
            var repairable = true;

            if (adapter.OriginalV4.Length == 0 || adapter.OriginalV6.Length == 0)
            {
                var resetIndex = adapter.InterfaceIndexV4 > 0
                    ? adapter.InterfaceIndexV4
                    : adapter.InterfaceIndexV6;

                if (resetIndex > 0)
                {
                    ordinals.Add(writes.Count);
                    writes.Add(new DnsWrite(resetIndex, null));
                }
                else
                {
                    repairable = false;
                }
            }

            if (adapter.OriginalV4.Length > 0)
            {
                if (adapter.InterfaceIndexV4 > 0)
                {
                    ordinals.Add(writes.Count);
                    writes.Add(new DnsWrite(adapter.InterfaceIndexV4, adapter.OriginalV4));
                }
                else
                {
                    repairable = false;
                }
            }

            if (adapter.OriginalV6.Length > 0)
            {
                if (adapter.InterfaceIndexV6 > 0)
                {
                    ordinals.Add(writes.Count);
                    writes.Add(new DnsWrite(adapter.InterfaceIndexV6, adapter.OriginalV6));
                }
                else
                {
                    repairable = false;
                }
            }

            perAdapter.Add((adapter, ordinals, repairable));
        }

        var outcome = await ApplyWritesAsync(writes, flushCache: true, cancellationToken).ConfigureAwait(false);

        foreach (var (adapter, ordinals, repairable) in perAdapter)
        {
            if (repairable && ordinals.TrueForAll(ordinal => outcome[ordinal]))
            {
                continue;
            }

            remaining.Add(adapter);
            unrepaired++;
        }

        if (remaining.Count > 0)
        {
            // Never discard the only recovery data merely because one adapter or one
            // PowerShell invocation failed. Successful rows are removed so a missing
            // old USB/VPN adapter cannot make every future restore repeat changes on
            // adapters that are already healthy.
            PersistSnapshot(remaining);

            if (unrepaired > 0)
            {
                throw new InvalidOperationException(
                    $"DNS ayarları {unrepaired} bağdaştırıcıda geri yüklenemedi; kurtarma bilgisi korundu.");
            }

            // Everything this machine actually has is back on its own servers. What is
            // left belongs to adapters that are not here, so the caller is told the
            // resolvers are the system's again - the alternative is refusing to start
            // protection over a repair nobody can perform.
            _log?.Invoke($"Original DNS configuration restored; {remaining.Count} absent adapter(s) still recorded.");
            CurrentMode = DnsMode.SystemDefault;
            return;
        }

        DeleteSnapshot();
        CurrentMode = DnsMode.SystemDefault;
        _log?.Invoke("Original DNS configuration restored.");
    }

    /// <summary>
    /// Combines the durable originals with the adapters visible in this run.
    /// Existing originals are never overwritten by the loopback values a previous
    /// run installed; only their current interface indexes are refreshed.
    /// </summary>
    internal static IReadOnlyList<AdapterDnsSnapshot> MergeSnapshots(
        IReadOnlyList<AdapterDnsSnapshot> saved,
        IReadOnlyList<AdapterDnsSnapshot> current)
    {
        var merged = new Dictionary<string, AdapterDnsSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var snapshot in saved)
        {
            // Old builds did not normalise duplicate rows. Keeping the first one is
            // important: it is the earliest, and therefore the most likely to contain
            // the real pre-redirect DNS values.
            merged.TryAdd(SnapshotKey(snapshot), snapshot);
        }

        foreach (var adapter in current)
        {
            var key = SnapshotKey(adapter);
            if (merged.TryGetValue(key, out var original))
            {
                merged[key] = original with
                {
                    Name = adapter.Name,
                    InterfaceIndexV4 = adapter.InterfaceIndexV4,
                    InterfaceIndexV6 = adapter.InterfaceIndexV6,
                };
            }
            else
            {
                merged[key] = adapter;
            }
        }

        return [.. merged.Values];
    }

    private static string SnapshotKey(AdapterDnsSnapshot snapshot)
        => !string.IsNullOrWhiteSpace(snapshot.Id) ? $"id:{snapshot.Id}" : $"name:{snapshot.Name}";

    /// <summary>
    /// Every interface index Windows currently knows about, in either family, including
    /// the adapters that are down.
    /// </summary>
    /// <remarks>
    /// Used to tell "this restore failed" apart from "there is nothing here to restore".
    /// Down adapters are deliberately included: one that is disabled still exists and
    /// still carries whatever DNS servers were last written to it.
    /// </remarks>
    private static HashSet<int> CurrentInterfaceIndexes()
    {
        var indexes = new HashSet<int>();

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                // Per adapter, so one interface that will not describe itself costs
                // only its own row rather than the whole answer.
                try
                {
                    var properties = nic.GetIPProperties();

                    try
                    {
                        indexes.Add(properties.GetIPv4Properties().Index);
                    }
                    catch (Exception)
                    {
                        // No IPv4 on this interface.
                    }

                    try
                    {
                        indexes.Add(properties.GetIPv6Properties().Index);
                    }
                    catch (Exception)
                    {
                        // No IPv6 on this interface.
                    }
                }
                catch (Exception)
                {
                    // Nothing readable about this one; the others still count.
                }
            }
        }
        catch (NetworkInformationException)
        {
            // "We could not ask" must not read as "none of them are here", or every
            // adapter would be written off as absent. An empty set is the caller's
            // signal to treat every row as present, which is what it did before.
            return [];
        }

        return indexes;
    }

    /// <summary>Every adapter that currently carries traffic, with both address family indexes.</summary>
    public async Task<IReadOnlyList<AdapterDnsSnapshot>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await EnumerateAsync(NetworkInterface.GetAllNetworkInterfaces(),
            (script, token) => ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(30), token),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<IReadOnlyList<AdapterDnsSnapshot>> EnumerateAsync(
        IEnumerable<NetworkInterface> interfaces,
        Func<string, CancellationToken, Task<ProcessResult>> probeDns,
        CancellationToken cancellationToken = default)
    {
        var result = new List<AdapterDnsSnapshot>();

        cancellationToken.ThrowIfCancellationRequested();
        var live = interfaces
            .Where(nic => nic.OperationalStatus == OperationalStatus.Up
                && nic.NetworkInterfaceType != NetworkInterfaceType.Loopback
                && nic.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
            .ToList();

        // GetAdaptersAddresses already supplies DNS servers and both interface
        // indexes through .NET. Avoid a cold PowerShell/module load on every start.
        // If any adapter cannot be read completely, use the existing probe for that
        // adapter; never manufacture an empty recovery snapshot from a failed read.
        var pending = new List<NetworkInterface>();
        foreach (var nic in live)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryCaptureAdapter(nic) is { } snapshot) result.Add(snapshot);
            else pending.Add(nic);
        }

        if (pending.Count == 0) return result;

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

        var probe = await probeDns(script, cancellationToken).ConfigureAwait(false);
        var rows = ParseRows(probe.StandardOutput);

        foreach (var nic in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private static AdapterDnsSnapshot? TryCaptureAdapter(NetworkInterface nic)
    {
        try
        {
            var properties = nic.GetIPProperties();
            var v4 = nic.Supports(NetworkInterfaceComponent.IPv4)
                ? properties.GetIPv4Properties()?.Index ?? 0 : 0;
            var supportsV6 = nic.Supports(NetworkInterfaceComponent.IPv6);
            var v6 = supportsV6 ? properties.GetIPv6Properties()?.Index ?? 0 : 0;

            // Keep the existing IPv4-capable adapter policy and fall back if a
            // supported family has no index (e.g. an adapter changing state).
            if (v4 <= 0 || (supportsV6 && v6 <= 0)) return null;

            var servers = properties.DnsAddresses;
            return new AdapterDnsSnapshot(nic.Id, nic.Name, v4, v6,
                FilterOurOwn(servers.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork)
                    .Select(ip => ip.ToString()).ToArray()),
                FilterOurOwn(servers.Where(ip => ip.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(ip => ip.ToString()).ToArray()));
        }
        catch (Exception ex) when (ex is NetworkInformationException or NotSupportedException)
        {
            return null;
        }
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

    /// <summary>
    /// One adapter change: the servers to set, or nothing at all to clear them.
    /// </summary>
    /// <param name="InterfaceIndex">The kernel index the cmdlet is scoped to.</param>
    /// <param name="Servers">The addresses to install, or null for a reset.</param>
    internal readonly record struct DnsWrite(int InterfaceIndex, string[]? Servers)
    {
        public bool IsReset => Servers is null;
    }

    /// <summary>
    /// Applies every adapter change in one PowerShell process, then flushes the cache.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the difference between protection starting in about a second and the
    /// application sitting on "Başlatılıyor…" for the best part of a minute. Each of
    /// these writes used to be its own <c>powershell.exe</c>, and so did the cache
    /// flush - so a machine with Wi-Fi, Ethernet and a couple of virtual adapters paid
    /// for seven or eight cold PowerShell starts, back to back, in front of the user,
    /// before anything was protected. The cmdlet calls themselves are milliseconds; the
    /// process launches and the DnsClient module load were the whole of the wait.
    /// </para>
    /// <para>
    /// Results come back one line per operation, in the order they were sent, so a
    /// single adapter refusing is still reported as that adapter refusing rather than
    /// collapsing the batch into one pass/fail.
    /// </para>
    /// </remarks>
    /// <returns>One flag per write, in the order given.</returns>
    internal static async Task<bool[]> ApplyWritesAsync(
        IReadOnlyList<DnsWrite> writes,
        bool flushCache,
        CancellationToken cancellationToken)
    {
        var outcome = new bool[writes.Count];

        if (writes.Count == 0)
        {
            if (flushCache)
            {
                await FlushCacheAsync(cancellationToken).ConfigureAwait(false);
            }

            return outcome;
        }

        var result = await ProcessRunner
            .PowerShellAsync(BuildWriteScript(writes, flushCache), WriteBudget(writes.Count), cancellationToken)
            .ConfigureAwait(false);

        foreach (var line in result.StandardOutput.Split('\n'))
        {
            // "dns <ordinal> <0|1>", printed by the script for every operation it ran.
            var parts = line.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 3
                && parts[0].Equals("dns", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(parts[1], out var ordinal)
                && ordinal >= 0
                && ordinal < outcome.Length)
            {
                outcome[ordinal] = parts[2] == "1";
            }
        }

        return outcome;
    }

    /// <summary>
    /// A budget that grows with the batch, because one refusal must not eat the rest.
    /// </summary>
    /// <remarks>
    /// The fixed part covers the process start and the DnsClient module load, which is
    /// most of the time a healthy batch spends. The per-write part is generous because
    /// an adapter that is mid-reconnect can make one cmdlet block for seconds.
    /// </remarks>
    private static TimeSpan WriteBudget(int writes)
        => TimeSpan.FromSeconds(Math.Clamp(20 + (4 * writes), 20, 120));

    internal static string BuildWriteScript(IReadOnlyList<DnsWrite> writes, bool flushCache)
    {
        var script = new StringBuilder();

        for (var i = 0; i < writes.Count; i++)
        {
            var write = writes[i];

            // Both interpolations are machine values, never user text: an integer the
            // kernel handed us, and addresses that have been through IPAddress.Parse.
            var command = write.IsReset
                ? $"Set-DnsClientServerAddress -InterfaceIndex {write.InterfaceIndex} -ResetServerAddresses -ErrorAction Stop"
                : $"Set-DnsClientServerAddress -InterfaceIndex {write.InterfaceIndex} "
                    + $"-ServerAddresses ({FormatServers(write.Servers!)}) -ErrorAction Stop";

            script.Append("try { ")
                .Append(command)
                .Append("; Write-Output 'dns ").Append(i).Append(" 1' } catch { Write-Output 'dns ")
                .Append(i)
                .Append(" 0' }; ");
        }

        if (flushCache)
        {
            script.Append("Clear-DnsClientCache -ErrorAction SilentlyContinue; ");
        }

        return script.ToString();
    }

    /// <summary>
    /// Renders addresses for the cmdlet, dropping anything that is not an IP literal.
    /// </summary>
    /// <remarks>
    /// The values come from a snapshot file on disk, so they are only as trustworthy as
    /// that file. Parsing each one before it reaches a PowerShell command line means a
    /// hand-edited or corrupted snapshot can cost a restore, which it always could, but
    /// can never become script.
    /// </remarks>
    private static string FormatServers(IEnumerable<string> servers)
        => string.Join(',', servers
            .Where(server => IPAddress.TryParse(server, out _))
            .Select(server => $"'{server}'"));

    public static Task FlushCacheAsync(CancellationToken cancellationToken = default)
        => ProcessRunner.PowerShellAsync("Clear-DnsClientCache -ErrorAction SilentlyContinue", TimeSpan.FromSeconds(15), cancellationToken);

    /// <summary>
    /// Atomically persists and reads back the recovery data before DNS is changed.
    /// Failure is fatal to the apply operation: running without a verified snapshot
    /// would make a process crash capable of taking name resolution down with it.
    /// </summary>
    private void PersistSnapshot(IReadOnlyList<AdapterDnsSnapshot> adapters)
    {
        var temporary = _snapshotPath + ".tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_snapshotPath)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(adapters, JsonOptions));

            // Verify the exact bytes that will become the recovery source. A full disk,
            // an interrupted write or an unexpected serializer result is discovered
            // while the system still has its original DNS settings.
            var verification = DeserializeSnapshot(File.ReadAllText(temporary));
            if (verification is null || verification.Count != adapters.Count)
            {
                throw new IOException("DNS snapshot verification failed.");
            }

            File.Move(temporary, _snapshotPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _log?.Invoke($"Could not persist DNS snapshot: {ex.Message}");
            TryDelete(temporary);
            throw new IOException(
                "DNS kurtarma bilgisi güvenli biçimde kaydedilemedi; sistem DNS ayarları değiştirilmedi.",
                ex);
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
            return DeserializeSnapshot(File.ReadAllText(_snapshotPath));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IReadOnlyList<AdapterDnsSnapshot>? DeserializeSnapshot(string json)
    {
        var snapshots = JsonSerializer.Deserialize<List<AdapterDnsSnapshot?>>(json);
        if (snapshots is null || snapshots.Any(snapshot => snapshot is null))
        {
            return null;
        }

        var normalised = new List<AdapterDnsSnapshot>(snapshots.Count);
        foreach (var snapshot in snapshots)
        {
            if (snapshot is null
                || (snapshot.InterfaceIndexV4 <= 0 && snapshot.InterfaceIndexV6 <= 0)
                || (string.IsNullOrWhiteSpace(snapshot.Id) && string.IsNullOrWhiteSpace(snapshot.Name)))
            {
                return null;
            }

            normalised.Add(snapshot with
            {
                Id = snapshot.Id ?? string.Empty,
                Name = snapshot.Name ?? string.Empty,
                OriginalV4 = FilterOurOwn(snapshot.OriginalV4),
                OriginalV6 = FilterOurOwn(snapshot.OriginalV6),
            });
        }

        return normalised;
    }

    /// <summary>
    /// Last-resort recovery for an unreadable snapshot. Only adapters whose configured
    /// servers consist entirely of this application's loopback addresses are reset;
    /// a legitimate local resolver accompanied by any other server is left alone.
    /// </summary>
    private static async Task<bool> RecoverOrphanedLoopbackAsync(CancellationToken cancellationToken)
    {
        const string script = """
            $rows = @(Get-DnsClientServerAddress -ErrorAction SilentlyContinue)
            $indexes = @($rows | ForEach-Object { $_.InterfaceIndex } | Where-Object { $_ -gt 0 } | Sort-Object -Unique)
            foreach ($index in $indexes) {
              $servers = @($rows |
                Where-Object { $_.InterfaceIndex -eq $index } |
                ForEach-Object { @($_.ServerAddresses) } |
                Where-Object { -not [string]::IsNullOrWhiteSpace([string]$_) })
              $foreign = @($servers | Where-Object { $_ -ne '127.0.0.1' -and $_ -ne '::1' })
              if ($servers.Count -gt 0 -and $foreign.Count -eq 0) {
                Set-DnsClientServerAddress -InterfaceIndex $index -ResetServerAddresses -ErrorAction Stop
              }
            }
            """;

        var result = await ProcessRunner.PowerShellAsync(script, TimeSpan.FromSeconds(30), cancellationToken)
            .ConfigureAwait(false);
        return result.Success;
    }

    private void PreserveUnreadableSnapshot()
    {
        try
        {
            if (File.Exists(_snapshotPath))
            {
                File.Move(_snapshotPath, _snapshotPath + ".bad", overwrite: true);
            }
        }
        catch (Exception)
        {
            // If it cannot be moved, deleting only the marker is still safer than
            // treating the same unreadable recovery source as valid on every launch.
            TryDelete(_snapshotPath);
        }
    }

    private void DeleteSnapshot()
    {
        TryDelete(_snapshotPath);
        TryDelete(_snapshotPath + ".tmp");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception)
        {
            // Left behind; the next recovery will retry it.
        }
    }

    private readonly record struct Row(int Index, string Alias, string Family, string[] Servers);
}
