using System.Text.Json;
using System.Text.Json.Serialization;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Logging;
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

public sealed record AppSettings
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

    public List<string> ExtraDomains { get; set; } = [];

    public List<string> ExcludedDomains { get; set; } = [];

    // --- Vodafone unlimited mode -------------------------------------------------

    /// <summary>Master switch for the hotspot TTL fix.</summary>
    public bool HotspotTtlFix { get; set; }

    /// <summary>Networks the fix is allowed to run on, newest last.</summary>
    public List<TtlFixNetwork> HotspotTtlNetworks { get; set; } = [];

    /// <summary>Advanced; 65 is correct for a phone that routes once.</summary>
    public byte HotspotTtlValue { get; set; } = Vodafone.TtlFixSettings.DefaultTimeToLive;

    public bool HotspotDropIPv6 { get; set; } = true;

    public TtlFixSettings BuildTtlFixSettings() => new()
    {
        TimeToLive = HotspotTtlValue,
        DropIPv6 = HotspotDropIPv6,
    };

    public bool HotspotNetworkRegistered(string key)
        => !string.IsNullOrEmpty(key) && HotspotTtlNetworks.Any(n => n.Key == key);

    /// <summary>Remembers a network, refreshing it if already known and dropping the oldest.</summary>
    public void RememberHotspotNetwork(string key, string displayName, string adapterName)
    {
        if (string.IsNullOrEmpty(key))
        {
            return;
        }

        HotspotTtlNetworks.RemoveAll(n => n.Key == key);
        HotspotTtlNetworks.Add(new TtlFixNetwork
        {
            Key = key,
            DisplayName = displayName,
            AdapterName = adapterName,
        });

        while (HotspotTtlNetworks.Count > Vodafone.TtlFixSettings.MaxNetworks)
        {
            HotspotTtlNetworks.RemoveAt(0);
        }
    }

    public bool ForgetHotspotNetwork(string key) => HotspotTtlNetworks.RemoveAll(n => n.Key == key) > 0;

    /// <summary>Per network results, keyed by <see cref="NetworkProfile.Key"/>.</summary>
    [JsonIgnore]
    public Dictionary<string, NetworkProfile> Networks { get; set; } = [];
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
        settings.ExtraDomains = CleanDomains(settings.ExtraDomains);
        settings.ExcludedDomains = CleanDomains(settings.ExcludedDomains);

        // A null entry throws in every lookup that walks this list, and a keyless one can
        // never name a network anyway.
        var hotspotNetworks = settings.HotspotTtlNetworks;
        settings.HotspotTtlNetworks = hotspotNetworks is null
            ? []
            : [.. hotspotNetworks
                .Where(network => network is not null && !string.IsNullOrWhiteSpace(network.Key))
                .Select(network => network with
                {
                    DisplayName = network.DisplayName ?? string.Empty,
                    AdapterName = network.AdapterName ?? string.Empty,
                })];

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

    private static List<string> CleanDomains(List<string>? domains) => domains is null
        ? []
        : [.. domains.Where(domain => !string.IsNullOrWhiteSpace(domain))];

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
