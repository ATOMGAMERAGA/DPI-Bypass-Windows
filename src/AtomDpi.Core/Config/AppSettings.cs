using System.Text.Json;
using System.Text.Json.Serialization;
using AtomDpi.Core.Dns;
using AtomDpi.Core.Engine;

namespace AtomDpi.Core.Config;

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

    public List<string> ExtraDomains { get; set; } = [];

    public List<string> ExcludedDomains { get; set; } = [];

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
