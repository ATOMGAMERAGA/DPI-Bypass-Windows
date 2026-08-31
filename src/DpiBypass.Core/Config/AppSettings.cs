using System.Text.Json;
using System.Text.Json.Serialization;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Logging;
using DpiBypass.Core.MobileHotspot;
using DpiBypass.Core.Network;

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
    /// Keeps the Vodafone-branded compatibility feature available without enabling the
    /// retired TTL/accounting rewrite.
    /// </summary>
    public bool VodafoneModeEnabled { get; set; }

    /// <summary>Networks the user associated with the safe Vodafone mode, newest last.</summary>
    public List<VodafoneModeNetwork> VodafoneModeNetworks { get; set; } = [];

    /// <summary>
    /// Run the read-only hotspot checks by themselves after a network change.
    /// </summary>
    /// <remarks>
    /// The checks - addressing, reachability, DNS, MTU - are what replaced the TTL
    /// rewrite, and they are always available on demand. This only decides whether
    /// moving to a Vodafone-mode network also runs them and logs the answer. It is
    /// switched on automatically for anyone who had the old mode enabled.
    /// See <see cref="HotspotLegacyMigration"/>.
    /// </remarks>
    public bool HotspotDiagnostics { get; set; }

    /// <summary>When the retired hotspot TTL configuration was cleaned out of this file.</summary>
    public DateTimeOffset? HotspotLegacyMigratedAt { get; set; }

    /// <summary>
    /// Marks the one-time correction for settings already processed by PR #11.
    /// </summary>
    public DateTimeOffset? VodafoneModeRestoredAt { get; set; }

    // --- Retired: the hotspot TTL rewrite -------------------------------------------
    // Kept as fields only so a settings file written by an older build is recognised and
    // cleaned rather than silently carried forward. Nothing reads them for behaviour;
    // ConfigStore runs the migration on every load, so an old file - or a restored
    // backup, or a hand edit - can never switch the rewrite back on.

    /// <summary>Legacy master switch. Always false after a load.</summary>
    public bool HotspotTtlFix { get; set; }

    /// <summary>Legacy per network list. Always empty after a load.</summary>
    public List<LegacyHotspotNetwork> HotspotTtlNetworks { get; set; } = [];

    /// <summary>Legacy rewrite value, recognized only so migration can remove it.</summary>
    public JsonElement? HotspotTtlValue { get; set; }

    /// <summary>Legacy IPv6-drop option, recognized only so migration can remove it.</summary>
    public JsonElement? HotspotDropIPv6 { get; set; }

    public bool VodafoneNetworkRegistered(string key)
        => !string.IsNullOrEmpty(key) && VodafoneModeNetworks.Any(network => network.Key == key);

    /// <summary>Remembers a Vodafone-mode network without tying it to packet rewriting.</summary>
    public void RememberVodafoneNetwork(string key, string displayName, string adapterName)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return;
        }

        VodafoneModeNetworks.RemoveAll(network => network.Key == key);
        VodafoneModeNetworks.Add(new VodafoneModeNetwork
        {
            Key = key,
            DisplayName = displayName,
            AdapterName = adapterName,
        });

        const int maximumRememberedNetworks = 10;
        while (VodafoneModeNetworks.Count > maximumRememberedNetworks)
        {
            VodafoneModeNetworks.RemoveAt(0);
        }
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

    public void Save(AppSettings settings)
    {
        lock (_gate)
        {
            WriteJson(_settingsPath, settings);
            WriteJson(_profilesPath, settings.Networks);
        }
    }

    public void SaveNetworks(AppSettings settings)
    {
        lock (_gate)
        {
            WriteJson(_profilesPath, settings.Networks);
        }
    }

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

        // Every load, not once from a marker: a restored backup or a hand edit must not
        // be able to bring the retired TTL rewrite back. The pass is idempotent, so
        // running it on a file it has already cleaned changes nothing.
        var migration = HotspotLegacyMigration.Apply(settings, DateTimeOffset.UtcNow);
        settings.LegacyHotspotCleaned = migration.Changed;

        if (migration.Changed)
        {
            AppLog.Info($"Eski hotspot yapılandırması bulundu ve temizlendi. {migration.Summary}");
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

    private static void WriteJson<T>(string path, T value)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temporary = path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(value, Options));

            if (File.Exists(path))
            {
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, path);
            }
        }
        catch (Exception)
        {
            // Settings that cannot be persisted still apply for this session.
        }
    }
}
