using System.Text.Json;
using System.Text.Json.Serialization;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Engine;
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

    private static T? ReadJson<T>(string path)
        where T : class
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options);
        }
        catch (Exception)
        {
            // A corrupt file must not stop the app; defaults are always usable.
            return null;
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
