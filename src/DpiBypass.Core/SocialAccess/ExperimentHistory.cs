using System.Text.Json;

namespace DpiBypass.Core.SocialAccess;

public sealed record HistorySummary(DateTimeOffset At, AttemptResult[] Results);
public sealed record HistoryFile(HistorySummary[] Entries, string[] SuppressedTargets, DateTimeOffset? CooldownUntilUtc = null);
public sealed class ExperimentHistory(string path)
{
    public HashSet<string> SuppressedTargets { get; } = [];
    public DateTimeOffset? CooldownUntilUtc { get; private set; }
    public void Load()
    {
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return;
            var value = JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(path), SocialJson.Options);
            if (value is not null)
            {
                SuppressedTargets.UnionWith(value.SuppressedTargets.Where(k => k.Length == 64 && k.All(Uri.IsHexDigit)));
                CooldownUntilUtc = value.CooldownUntilUtc;
            }
            Save([]);
        }
        catch (Exception) { }
    }
    public void Save(IReadOnlyList<AttemptResult> results, DateTimeOffset? cooldownUntilUtc = null)
    {
        try
        {
            var entries = new List<HistorySummary>();
            if (File.Exists(path) && new FileInfo(path).Length <= 1024 * 1024)
                entries.AddRange(JsonSerializer.Deserialize<HistoryFile>(File.ReadAllText(path), SocialJson.Options)?.Entries ?? []);
            entries.RemoveAll(e => e.At < DateTimeOffset.UtcNow.AddDays(-14));
            if (results.Count > 0) entries.Add(new(DateTimeOffset.UtcNow, results.ToArray()));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            CooldownUntilUtc = cooldownUntilUtc ?? CooldownUntilUtc;
            File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new HistoryFile(entries.TakeLast(100).ToArray(), SuppressedTargets.ToArray(), CooldownUntilUtc), SocialJson.Options));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception) { /* Diagnostic retention must never compromise recovery. */ }
    }
    public void Clear()
    {
        // Diagnostic deletion cannot bypass a rate limit or silently re-enable a
        // target/observer combination that the user reported as a false positive.
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(
            new HistoryFile([], SuppressedTargets.ToArray(), CooldownUntilUtc), SocialJson.Options));
        File.Move(path + ".tmp", path, true);
    }
}
