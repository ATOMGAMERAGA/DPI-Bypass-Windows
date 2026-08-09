using System.Text.Json;

namespace DpiBypass.Core.Config;

/// <summary>
/// The hostnames the app worked out are filtered here, by measuring rather than
/// being told.
/// </summary>
/// <remarks>
/// Kept separate from the settings file on purpose: this list is something the app
/// discovers and prunes on its own, and mixing it into the user's configuration
/// would make a hand-edited settings file churn on every run.
/// </remarks>
public sealed class LearnedDomainStore
{
    /// <summary>Beyond this the oldest entries are dropped.</summary>
    public const int Capacity = 200;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private readonly string _path;
    private readonly Lock _gate = new();
    private readonly List<string> _domains = [];

    public LearnedDomainStore(string? path = null)
    {
        _path = path ?? AppPaths.LearnedDomainsFile;
        Load();
    }

    public IReadOnlyList<string> Domains
    {
        get
        {
            lock (_gate)
            {
                return [.. _domains];
            }
        }
    }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _domains.Count;
            }
        }
    }

    public bool Contains(string domain)
    {
        var normalised = Normalise(domain);
        if (normalised is null)
        {
            return false;
        }

        lock (_gate)
        {
            return _domains.Contains(normalised);
        }
    }

    /// <summary>Adds a domain. Returns false when it was already known or unusable.</summary>
    public bool Add(string domain)
    {
        var normalised = Normalise(domain);
        if (normalised is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (_domains.Contains(normalised))
            {
                return false;
            }

            _domains.Add(normalised);
            while (_domains.Count > Capacity)
            {
                _domains.RemoveAt(0);
            }

            Save();
            return true;
        }
    }

    public bool Remove(string domain)
    {
        var normalised = Normalise(domain);
        if (normalised is null)
        {
            return false;
        }

        lock (_gate)
        {
            if (!_domains.Remove(normalised))
            {
                return false;
            }

            Save();
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _domains.Clear();
            Save();
        }
    }

    private static string? Normalise(string? domain)
    {
        var trimmed = domain?.Trim().Trim('.').ToLowerInvariant();
        return string.IsNullOrEmpty(trimmed) || !trimmed.Contains('.') ? null : trimmed;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(_path), Options);
            if (loaded is null)
            {
                return;
            }

            foreach (var domain in loaded)
            {
                var normalised = Normalise(domain);
                if (normalised is not null && !_domains.Contains(normalised))
                {
                    _domains.Add(normalised);
                }
            }
        }
        catch (Exception)
        {
            // A corrupt list just means we relearn.
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_domains, Options));

            if (File.Exists(_path))
            {
                File.Replace(temporary, _path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporary, _path);
            }
        }
        catch (Exception)
        {
            // In-memory list still works for this session.
        }
    }
}
