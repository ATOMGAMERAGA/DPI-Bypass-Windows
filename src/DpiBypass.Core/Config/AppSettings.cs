using System.Text.Json;
using System.Text.Json.Serialization;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Logging;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;
using DpiBypass.Core.Vodafone;

namespace DpiBypass.Core.Config;

/// <summary>What we remembered about one network, so returning to it is instant.</summary>
public sealed record NetworkProfile
{
    public required string Key { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public string? IspProfileId { get; init; }

    public string? StrategyId { get; init; }

    public bool WasUnfiltered { get; init; }

    public DateTimeOffset LastVerified { get; init; }

    public int SuccessCount { get; init; }

    public int FailureCount { get; init; }
}

/// <summary>
/// What the latency feature has been told to measure, and how far it may go.
/// </summary>
/// <remarks>
/// Kept as its own record so a settings file written by an older build - which has none
/// of this - loads with every field at its safe default rather than failing to parse.
/// Nothing here is identifying beyond what the user typed themselves.
/// </remarks>
public sealed record LatencyPreferences
{
    public LatencyTargetKind TargetKind { get; set; } = LatencyTargetKind.Reference;

    /// <summary>Host or address for a custom target, exactly as the user typed it.</summary>
    public string? TargetHost { get; set; }

    public int? TargetPort { get; set; }

    public LatencyProtocol TargetProtocol { get; set; } = LatencyProtocol.Icmp;

    /// <summary>Executable name (no path) of the application target.</summary>
    public string? TargetProcess { get; set; }

    /// <summary>
    /// Whether the loaded-latency lane may create a send-rate limit.
    /// </summary>
    /// <remarks>
    /// Off by default. It creates a QoS policy, and a policy that appears without being
    /// asked for is exactly the kind of thing a user should never find on their machine.
    /// </remarks>
    public bool TrafficGuardEnabled { get; set; }

    /// <summary>The one application whose bulk sending may be paced.</summary>
    public string? TrafficGuardApplication { get; set; }

    /// <summary>Uplink capacity in Mbit/s when the user knows it better than we can measure.</summary>
    public double? ManualUplinkMbps { get; set; }

    /// <summary>Downlink capacity in Mbit/s, for the same reason.</summary>
    public double? ManualDownlinkMbps { get; set; }

    /// <summary>
    /// Whether the user agreed to controlled adapter restarts during a measurement.
    /// </summary>
    /// <remarks>
    /// Off by default and never inferred. Most NDIS advanced keywords only take effect
    /// once the miniport restarts, which drops every connection on it for a few seconds -
    /// a fair trade to measure a setting, and never something to do unasked.
    /// </remarks>
    public bool AllowAdapterRestart { get; set; }

    /// <summary>
    /// Which trade-off the send-rate cap search optimises for.
    /// </summary>
    /// <remarks>
    /// Balanced keeps as much of the transfer as it can while emptying the queue; lowest
    /// latency accepts a slower transfer, and shows the user what that cost.
    /// </remarks>
    public TrafficGuardMode GuardMode { get; set; } = TrafficGuardMode.Balanced;

    /// <summary>The discovered endpoint the user pinned, as "address:port".</summary>
    public string? PinnedEndpoint { get; set; }

    public LatencyTargetSpec ToSpec() => TargetKind switch
    {
        LatencyTargetKind.Custom when !string.IsNullOrWhiteSpace(TargetHost) => new LatencyTargetSpec
        {
            Kind = LatencyTargetKind.Custom,
            Host = TargetHost,
            Port = TargetPort,
            Protocol = TargetProtocol,
        },
        LatencyTargetKind.Application when !string.IsNullOrWhiteSpace(TargetProcess) => new LatencyTargetSpec
        {
            PreferredEndpoint = PinnedEndpoint,
            Kind = LatencyTargetKind.Application,
            ProcessName = TargetProcess,
        },
        _ => LatencyTargetSpec.Reference,
    };

    public LinkCapacityEstimate ToCapacity() => LinkCapacityEstimate.FromUser(
        ManualUplinkMbps is { } uplink and > 0 ? uplink * 1000 : null,
        ManualDownlinkMbps is { } downlink and > 0 ? downlink * 1000 : null);

    /// <summary>
    /// What a run may do to a live adapter, given this machine as well as this setting.
    /// </summary>
    /// <remarks>
    /// The remote-session half is not a preference. Restarting the adapter carrying a
    /// Remote Desktop session takes the session away, so it is refused however the
    /// checkbox is set.
    /// </remarks>
    public AdapterRestartPolicy ToRestartPolicy() => new()
    {
        UserConsented = AllowAdapterRestart,
        RemoteSession = DpiBypass.Core.Interop.SessionKind.IsRemoteSession(),
    };
}

public sealed record AppSettings : IHotspotLegacyState
{
    /// <summary>Discord only, Discord plus browsers, or the whole machine.</summary>
    public ProtectionScope Scope { get; set; } = ProtectionScope.DiscordAndBrowsers;

    public bool StartEngineOnLaunch { get; set; } = true;

    public bool StartWithWindows { get; set; } = true;

    public bool StartMinimised { get; set; } = true;

    public bool MinimiseToTrayOnClose { get; set; } = true;

    /// <summary>
    /// Whether this installation has ever had its window in front of the user.
    /// </summary>
    /// <remarks>
    /// The logon task starts the app minimised, so without this a freshly installed
    /// copy would spend its whole life in the notification area - behind the Windows
    /// 11 overflow chevron, where a user who has never seen the app has no reason to
    /// look. Until the window has been shown once, every launch shows it.
    /// </remarks>
    public bool HasShownWindow { get; set; }

    /// <summary>
    /// Stops the app from ever asking Windows for the Mica material behind its window.
    /// </summary>
    /// <remarks>
    /// The material is drawn by the compositor, which means the window stops painting
    /// its own client area to let it through. On a machine where DWM accepts the request
    /// and then does not draw anything, the result is a window that is running, focused,
    /// listed in the taskbar and completely see-through - and nothing Windows will answer
    /// distinguishes that from a window being drawn perfectly. So the app sets this for
    /// itself when the user's own behaviour says they cannot see the window, and never
    /// tries the material on that machine again. Cosmetic either way.
    /// </remarks>
    public bool DisableWindowBackdrop { get; set; }

    public DnsMode DnsMode { get; set; } = DnsMode.EncryptedLoopback;

    public bool BlockQuicHandshakes { get; set; } = true;

    /// <summary>Empty means "detect the operator automatically".</summary>
    public string? ManualIspProfileId { get; set; }

    /// <summary>Empty means "let the tuner pick".</summary>
    public string? ManualStrategyId { get; set; }

    public bool AutoTuneOnNetworkChange { get; set; } = true;

    public bool VerifyAfterTuning { get; set; } = true;

    /// <summary>
    /// Silently test hostnames we have not seen before and add the ones that only
    /// open with the bypass. Mirrors the Linux build's discovery pass.
    /// </summary>
    public bool AutoDiscoverBlockedSites { get; set; } = true;

    /// <summary>How often to re-verify the chosen recipe, in seconds. Zero disables it.</summary>
    public int RecheckIntervalSeconds { get; set; } = 1800;

    /// <summary>
    /// Measure and test reversible NIC settings independently from the DPI engine.
    /// Missing in older JSON files means false through the normal serializer default.
    /// </summary>
    public bool LowLatencyMode { get; set; }

    /// <summary>Target, Traffic Guard and capacity choices for the latency feature.</summary>
    public LatencyPreferences Latency { get; set; } = new();

    public List<string> ExtraDomains { get; set; } = [];

    public List<string> ExcludedDomains { get; set; } = [];

    // --- Vodafone Sınırsız Modu / mobile hotspot diagnostics ----------------------

    /// <summary>
    /// The master switch for Vodafone Sınırsız Modu: the TTL rewrite and the checks.
    /// </summary>
    /// <remarks>
    /// On, plus one of <see cref="VodafoneModeNetworks"/> under us, is what installs the
    /// rewrite. The per-network gate is not decoration: rewriting TTLs on a home router,
    /// where nothing is counting hops, is a change to the user's traffic that buys them
    /// nothing.
    /// </remarks>
    public bool VodafoneModeEnabled { get; set; }

    /// <summary>Networks the user associated with Vodafone mode, newest last.</summary>
    public List<VodafoneModeNetwork> VodafoneModeNetworks { get; set; } = [];

    /// <summary>
    /// What outgoing packets leave with on a registered network.
    /// </summary>
    /// <remarks>
    /// 65 so the phone's own decrement leaves 64 on the wire. Out-of-range values are
    /// corrected on load rather than refused, so a hand-edited file cannot be the reason
    /// the mode quietly does nothing. Same default and same guard as the Linux build's
    /// <c>vodafone_ttl</c>.
    /// </remarks>
    public int VodafoneTtl { get; set; } = TtlFixSettings.DefaultTimeToLive;

    /// <summary>Drop outbound IPv6 on the shared adapter while the mode is active.</summary>
    /// <remarks>
    /// Tethering gives the laptop its own global IPv6 address, so one subscriber shows up
    /// as two sources whatever the hop limit says. The Linux build turns IPv6 off on the
    /// interface for the same reason.
    /// </remarks>
    public bool VodafoneDropIPv6 { get; set; } = true;

    /// <summary>
    /// Run the read-only hotspot checks by themselves after a network change.
    /// </summary>
    /// <remarks>
    /// The checks - addressing, reachability, DNS, MTU - explain the connection; the
    /// rewrite is what changes it. They are separate switches because they answer
    /// separate questions, and the checks are always available on demand.
    /// </remarks>
    public bool HotspotDiagnostics { get; set; }

    /// <summary>When the old field names were folded into the ones above.</summary>
    public DateTimeOffset? HotspotLegacyMigratedAt { get; set; }

    /// <summary>
    /// Marks the one-time correction for settings already processed by PR #11.
    /// </summary>
    public DateTimeOffset? VodafoneModeRestoredAt { get; set; }

    // --- Older field names for the same settings ------------------------------------
    // A settings file written before the Vodafone* names existed carries these instead.
    // ConfigStore folds them into the current fields on every load and then clears them,
    // so nothing downstream has to know two names for one setting.

    /// <summary>Legacy master switch. Folded into <see cref="VodafoneModeEnabled"/>.</summary>
    public bool HotspotTtlFix { get; set; }

    /// <summary>Legacy per network list. Folded into <see cref="VodafoneModeNetworks"/>.</summary>
    public List<LegacyHotspotNetwork> HotspotTtlNetworks { get; set; } = [];

    /// <summary>Legacy rewrite value. Folded into <see cref="VodafoneTtl"/>.</summary>
    public JsonElement? HotspotTtlValue { get; set; }

    /// <summary>Legacy IPv6 option. Folded into <see cref="VodafoneDropIPv6"/>.</summary>
    public JsonElement? HotspotDropIPv6 { get; set; }

    public bool VodafoneNetworkRegistered(string key)
        => !string.IsNullOrEmpty(key) && VodafoneModeNetworks.Any(network => network.Key == key);

    /// <summary>Whether the network we are on now is one of the remembered ones.</summary>
    public bool VodafoneNetworkRegistered(NetworkFingerprint network)
        => MatchVodafoneNetwork(network) is not null;

    /// <summary>
    /// Finds the remembered entry for a network, by identity first and by name second.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The exact key is the right answer whenever it is available, and it is the only
    /// thing this used to look at. That made the feature unusable for the connection it
    /// was written for: <see cref="NetworkFingerprint.Key"/> hashes the access point's
    /// MAC address, and a phone hotspot gets a fresh randomised one every time it is
    /// turned off and on, so the user's own saved network came back as an unknown one on
    /// the next connection - "kayıtlı değil" against the very network they had just
    /// registered, with no automatic check and nothing to do but register it again.
    /// </para>
    /// <para>
    /// So a wireless network also matches on its name. Two different links can share an
    /// SSID, which is why identity still wins when it matches, but the failure that
    /// costs the user something is the one above: a name match runs read-only checks on
    /// a network that is almost certainly theirs, while a missed match makes the whole
    /// feature look broken.
    /// </para>
    /// </remarks>
    public VodafoneModeNetwork? MatchVodafoneNetwork(NetworkFingerprint? network)
    {
        if (network is null)
        {
            return null;
        }

        var key = network.Key;
        var exact = VodafoneModeNetworks.FirstOrDefault(entry => entry.Key == key);
        if (exact is not null)
        {
            return exact;
        }

        var ssid = network.Ssid;
        if (string.IsNullOrWhiteSpace(ssid))
        {
            return null;
        }

        return VodafoneModeNetworks.FirstOrDefault(entry =>
            NameMatches(entry.Ssid, ssid) || NameMatches(entry.DisplayName, ssid));
    }

    private static bool NameMatches(string? remembered, string ssid)
        => !string.IsNullOrWhiteSpace(remembered)
            && string.Equals(remembered.Trim(), ssid.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Remembers a Vodafone-mode network without tying it to packet rewriting.</summary>
    public void RememberVodafoneNetwork(string key, string displayName, string adapterName, string? ssid = null)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        var name = ssid ?? string.Empty;

        // Re-registering the same network - a new session of the same hotspot, or the
        // user pressing "save this network" again - replaces the entry rather than
        // filling the list with one row per access point MAC.
        VodafoneModeNetworks.RemoveAll(network => network.Key == key
            || (name.Length > 0 && (NameMatches(network.Ssid, name) || NameMatches(network.DisplayName, name))));

        VodafoneModeNetworks.Add(new VodafoneModeNetwork
        {
            Key = key,
            Ssid = name,
            DisplayName = displayName,
            AdapterName = adapterName,
        });

        while (VodafoneModeNetworks.Count > TtlFixSettings.MaxNetworks)
        {
            VodafoneModeNetworks.RemoveAt(0);
        }
    }

    /// <summary>Remembers the network we are on now, keeping its name for later matching.</summary>
    public void RememberVodafoneNetwork(NetworkFingerprint network)
    {
        ArgumentNullException.ThrowIfNull(network);

        RememberVodafoneNetwork(
            network.Key,
            network.DisplayName,
            network.AdapterName ?? string.Empty,
            network.Ssid);
    }

    /// <summary>
    /// Points a remembered entry at the identity the network has right now.
    /// </summary>
    /// <remarks>
    /// Called when a network was recognised by name rather than by key, which is the
    /// normal case for a phone hotspot. Refreshing the stored key keeps every later
    /// lookup on the fast, exact path and keeps the saved-network list describing the
    /// connection the user is actually on.
    /// </remarks>
    /// <returns>True when something was written and the file is now behind.</returns>
    public bool RefreshVodafoneNetworkIdentity(NetworkFingerprint network)
    {
        var match = MatchVodafoneNetwork(network);
        if (match is null)
        {
            return false;
        }

        var adapter = network.AdapterName ?? string.Empty;
        var ssid = network.Ssid ?? string.Empty;

        if (match.Key == network.Key
            && match.AdapterName == adapter
            && match.DisplayName == network.DisplayName
            && match.Ssid == ssid)
        {
            return false;
        }

        var index = VodafoneModeNetworks.IndexOf(match);
        VodafoneModeNetworks[index] = match with
        {
            Key = network.Key,
            Ssid = ssid,
            DisplayName = network.DisplayName,
            AdapterName = adapter,
        };

        return true;
    }

    public bool ForgetVodafoneNetwork(string key)
        => VodafoneModeNetworks.RemoveAll(network => network.Key == key) > 0;

    /// <summary>Per network results, keyed by <see cref="NetworkProfile.Key"/>.</summary>
    [JsonIgnore]
    public Dictionary<string, NetworkProfile> Networks { get; set; } = [];

    /// <summary>
    /// True when the load that produced this object had to clean legacy hotspot state
    /// out of it, so the caller knows the file on disk is now behind.
    /// </summary>
    [JsonIgnore]
    public bool LegacyHotspotCleaned { get; internal set; }

    bool IHotspotLegacyState.LegacyTtlFixEnabled
    {
        get => HotspotTtlFix;
        set => HotspotTtlFix = value;
    }

    List<LegacyHotspotNetwork> IHotspotLegacyState.LegacyNetworks => HotspotTtlNetworks;

    JsonElement? IHotspotLegacyState.LegacyTtlValue
    {
        get => HotspotTtlValue;
        set => HotspotTtlValue = value;
    }

    JsonElement? IHotspotLegacyState.LegacyDropIpv6
    {
        get => HotspotDropIPv6;
        set => HotspotDropIPv6 = value;
    }

    bool IHotspotLegacyState.VodafoneModeEnabled
    {
        get => VodafoneModeEnabled;
        set => VodafoneModeEnabled = value;
    }

    List<VodafoneModeNetwork> IHotspotLegacyState.VodafoneNetworks => VodafoneModeNetworks;

    bool IHotspotLegacyState.DiagnosticsEnabled
    {
        get => HotspotDiagnostics;
        set => HotspotDiagnostics = value;
    }

    int IHotspotLegacyState.VodafoneTtl
    {
        get => VodafoneTtl;
        set => VodafoneTtl = value;
    }

    bool IHotspotLegacyState.VodafoneDropIpv6
    {
        get => VodafoneDropIPv6;
        set => VodafoneDropIPv6 = value;
    }

    DateTimeOffset? IHotspotLegacyState.LegacyMigratedAt
    {
        get => HotspotLegacyMigratedAt;
        set => HotspotLegacyMigratedAt = value;
    }

    DateTimeOffset? IHotspotLegacyState.VodafoneIdentityRestoredAt
    {
        get => VodafoneModeRestoredAt;
        set => VodafoneModeRestoredAt = value;
    }
}

/// <summary>
/// Loads and saves settings. Writes go through a temporary file and a replace so a
/// power cut cannot leave a half written settings file behind.
/// </summary>
/// <summary>Why a settings write did not land.</summary>
public enum ConfigSaveFailure
{
    None = 0,

    /// <summary>Windows refused the write: permissions, or a lock held by something else.</summary>
    AccessDenied = 1,

    /// <summary>No room left on the volume.</summary>
    DiskFull = 2,

    /// <summary>Any other I/O problem: a disconnected drive, a path that vanished.</summary>
    Io = 3,

    /// <summary>The settings could not be turned into JSON at all.</summary>
    Serialization = 4,
}

/// <summary>
/// What happened to a save.
/// </summary>
/// <remarks>
/// Returned rather than swallowed. A write that fails still leaves the setting applied
/// for this session - the user asked for it and it is doing what they asked - but they
/// were being told it was saved, and it was not there next launch. The caller now has
/// something to show and something to retry.
/// </remarks>
public readonly record struct ConfigSaveResult(ConfigSaveFailure Failure, string? Detail)
{
    public static readonly ConfigSaveResult Ok = new(ConfigSaveFailure.None, null);

    public bool Succeeded => Failure == ConfigSaveFailure.None;

    /// <summary>A short sentence for the UI, in the app's language.</summary>
    public string Describe() => Failure switch
    {
        ConfigSaveFailure.None => "Kaydedildi.",
        ConfigSaveFailure.AccessDenied =>
            "Bu oturumda uygulandı; ayar dosyasına yazma izni yok, kaydedilemedi.",
        ConfigSaveFailure.DiskFull =>
            "Bu oturumda uygulandı; diskte yer kalmadığı için kaydedilemedi.",
        ConfigSaveFailure.Serialization =>
            "Bu oturumda uygulandı; ayarlar dosyaya dönüştürülemediği için kaydedilemedi.",
        _ => "Bu oturumda uygulandı; kaydedilemedi.",
    };
}

public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _settingsPath;
    private readonly string _profilesPath;
    private readonly Lock _gate = new();

    public ConfigStore(string? settingsPath = null, string? profilesPath = null)
    {
        _settingsPath = settingsPath ?? AppPaths.SettingsFile;
        _profilesPath = profilesPath ?? AppPaths.ProfilesFile;
    }

    public AppSettings Load()
    {
        var settings = ReadJson<AppSettings>(_settingsPath) ?? new AppSettings();
        settings.Networks = ReadJson<Dictionary<string, NetworkProfile>>(_profilesPath) ?? [];
        Normalise(settings);
        return settings;
    }

    /// <summary>
    /// Writes both files and reports the first thing that went wrong.
    /// </summary>
    /// <remarks>
    /// The snapshot of the mutable collections is taken under the same lock as the write,
    /// so a network profile being recorded on a background thread cannot be half copied
    /// into the file the UI thread is serialising. The file lock alone never protected
    /// those collections - it is a lock on the writer, not on the data.
    /// </remarks>
    public ConfigSaveResult Save(AppSettings settings)
    {
        lock (_gate)
        {
            var settingsResult = WriteJson(_settingsPath, settings);
            var profilesResult = WriteJson(_profilesPath, Snapshot(settings.Networks));
            return settingsResult.Succeeded ? profilesResult : settingsResult;
        }
    }

    public ConfigSaveResult SaveNetworks(AppSettings settings)
    {
        lock (_gate)
        {
            return WriteJson(_profilesPath, Snapshot(settings.Networks));
        }
    }

    private static Dictionary<string, NetworkProfile> Snapshot(Dictionary<string, NetworkProfile> networks)
        => new(networks, StringComparer.Ordinal);

    /// <summary>
    /// A file that parses can still be wrong: <c>"ExtraDomains": null</c> is valid JSON,
    /// and the deserialiser writes that null straight over the property initialiser. The
    /// rest of the app treats these as always present - the matcher walks them on the
    /// packet path - so every load is put right here, the one place they all go through.
    /// </summary>
    private static void Normalise(AppSettings settings)
    {
        // "Latency": null is valid JSON and the deserialiser writes that null straight
        // over the property initialiser, so every load puts it back.
        settings.Latency ??= new LatencyPreferences();
        settings.Latency.TargetHost = Trim(settings.Latency.TargetHost);
        settings.Latency.TargetProcess = Trim(settings.Latency.TargetProcess);
        settings.Latency.TrafficGuardApplication = Trim(settings.Latency.TrafficGuardApplication);
        settings.Latency.PinnedEndpoint = Trim(settings.Latency.PinnedEndpoint);

        if (!Enum.IsDefined(settings.Latency.GuardMode))
        {
            settings.Latency.GuardMode = TrafficGuardMode.Balanced;
        }

        if (settings.Latency.TargetPort is < 1 or > 65535)
        {
            settings.Latency.TargetPort = null;
        }

        if (settings.Latency.ManualUplinkMbps is <= 0 or > 100_000 or double.NaN)
        {
            settings.Latency.ManualUplinkMbps = null;
        }

        if (settings.Latency.ManualDownlinkMbps is <= 0 or > 100_000 or double.NaN)
        {
            settings.Latency.ManualDownlinkMbps = null;
        }

        settings.ExtraDomains = CleanDomains(settings.ExtraDomains);
        settings.ExcludedDomains = CleanDomains(settings.ExcludedDomains);

        settings.VodafoneModeNetworks = CleanVodafoneNetworks(settings.VodafoneModeNetworks);

        // A null entry throws in every lookup that walks this list, and a keyless one can
        // never name a network anyway.
        settings.HotspotTtlNetworks = settings.HotspotTtlNetworks is null
            ? []
            : [.. settings.HotspotTtlNetworks.Where(network => network is not null)];

        // Every load, not once from a marker: a restored backup or a hand edit can carry
        // the old field names, and the pass is idempotent, so running it on a file it has
        // already folded changes nothing.
        var migration = HotspotLegacyMigration.Apply(settings, DateTimeOffset.UtcNow);
        settings.LegacyHotspotCleaned = migration.Changed;

        if (migration.Changed)
        {
            AppLog.Info($"Eski hotspot yapılandırması taşındı. {migration.Summary}");
        }

        // After the migration, because that is one of the places the number comes from.
        // A TTL under the guard would rewrite the engine's own low-TTL decoys and break
        // every fake-packet strategy, so an unusable value becomes the default instead of
        // becoming a silent fault the user cannot see.
        var ttl = TtlFixSettings.CoerceTimeToLive(settings.VodafoneTtl);
        if (ttl != settings.VodafoneTtl)
        {
            AppLog.Warning($"Vodafone TTL değeri geçersiz ({settings.VodafoneTtl}); {ttl} kullanılacak.");
            settings.VodafoneTtl = ttl;
            settings.LegacyHotspotCleaned = true;
        }

        settings.Networks = settings.Networks
            .Where(pair => pair.Value is not null && !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => pair.Key,
                pair => pair.Value with
                {
                    // The profile is looked up by the dictionary key, so that is the key
                    // it has, whatever the file says.
                    Key = pair.Key,
                    DisplayName = pair.Value.DisplayName ?? string.Empty,
                });
    }

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> CleanDomains(List<string>? domains) => domains is null
        ? []
        : [.. domains.Where(domain => !string.IsNullOrWhiteSpace(domain))];

    private static List<VodafoneModeNetwork> CleanVodafoneNetworks(List<VodafoneModeNetwork>? networks)
        => networks is null
            ? []
            : [.. networks
                .Where(network => network is not null && !string.IsNullOrWhiteSpace(network.Key))
                .Select(network => network with
                {
                    Ssid = network.Ssid ?? string.Empty,
                    DisplayName = network.DisplayName ?? string.Empty,
                    AdapterName = network.AdapterName ?? string.Empty,
                })];

    private static T? ReadJson<T>(string path)
        where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (JsonException ex)
        {
            // Silently falling back to defaults would let the next save overwrite a file
            // the user can still fix by hand, so it is kept and the loss is reported.
            AppLog.Warning($"'{Path.GetFileName(path)}' is not valid JSON ({ex.Message}); defaults are in use.");
            PreserveUnreadable(path);
            return null;
        }
        catch (Exception ex)
        {
            AppLog.Warning($"'{Path.GetFileName(path)}' could not be read ({ex.Message}); defaults are in use.");
            return null;
        }
    }

    private static void PreserveUnreadable(string path)
    {
        try
        {
            File.Move(path, path + ".bad", overwrite: true);
        }
        catch (Exception)
        {
            // Keeping the old file is a courtesy; defaults load either way.
        }
    }

    /// <summary>
    /// Writes atomically: a full temporary file first, then a replace.
    /// </summary>
    /// <remarks>
    /// The last good file is never truncated on the way to a failed write, so a disk that
    /// filled up half way through costs the newest preference rather than every setting
    /// the user ever chose. Failures are classified and returned; they used to be
    /// swallowed here, which is how a machine with a read-only profile directory could
    /// show a settings screen that reverted itself on every launch with no explanation.
    /// </remarks>
    private static ConfigSaveResult WriteJson<T>(string path, T value)
    {
        var temporary = path + ".tmp";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));

            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }

            return ConfigSaveResult.Ok;
        }
        catch (JsonException ex)
        {
            return Failed(temporary, ConfigSaveFailure.Serialization, path, ex);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Failed(temporary, ConfigSaveFailure.AccessDenied, path, ex);
        }
        catch (IOException ex)
        {
            // 0x27 ERROR_HANDLE_DISK_FULL, 0x70 ERROR_DISK_FULL. HResult carries the
            // Win32 code in its low word, and the two cases read very differently to a
            // user: one is "fix your permissions", the other is "free some space".
            var code = ex.HResult & 0xFFFF;
            var failure = code is 0x27 or 0x70 ? ConfigSaveFailure.DiskFull : ConfigSaveFailure.Io;
            return Failed(temporary, failure, path, ex);
        }
        catch (Exception ex)
        {
            return Failed(temporary, ConfigSaveFailure.Io, path, ex);
        }
    }

    private static ConfigSaveResult Failed(string temporary, ConfigSaveFailure failure, string path, Exception error)
    {
        // A half written temporary must not be left where a later run could mistake it
        // for something meaningful, and it costs space on the disk that just filled up.
        try
        {
            File.Delete(temporary);
        }
        catch (Exception)
        {
            // Best effort; the replace never happened, so the real file is still intact.
        }

        return new ConfigSaveResult(failure, $"{Path.GetFileName(path)}: {error.Message}");
    }
}
