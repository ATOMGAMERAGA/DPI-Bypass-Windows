using System.Text.Json;

namespace DpiBypass.Core.SocialAccess;

public sealed record JournalEntry(UndoRecord Undo, int Attempts, int Requests, long Bytes, double ActiveSeconds, int PageLoads);
public sealed class ExperimentJournal(string path)
{
    public bool Exists => File.Exists(path);
    public void Write(JournalEntry entry)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        var temporary = path + ".tmp";
        // No URL, account identifiers, DOM or network fingerprint enters this file.
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        { JsonSerializer.Serialize(stream, entry, SocialJson.Options); stream.Flush(true); }
        File.Move(temporary, path, overwrite: true);
    }
    public JournalEntry? Read()
    {
        if (!Exists) return null;
        if (new FileInfo(path).Length > 16384) throw new InvalidDataException("JOURNAL_INVALID");
        var entry = JsonSerializer.Deserialize<JournalEntry>(File.ReadAllText(path), SocialJson.Options);
        if (entry is null || entry.Undo is null || entry.Undo.SchemaVersion != 2
            || entry.Undo.HandlerId.Length is < 1 or > 128 || entry.Undo.OperationId.Length is < 1 or > 128
            || entry.Undo.Stage is not ("PREPARED" or "APPLYING" or "ACTIVE" or "KEPT" or "ROLLING_BACK")
            || entry.Attempts is < 0 or > 12 || entry.Requests is < 0 or > 24
            || entry.Bytes is < 0 or > 5242880 || entry.PageLoads is < 0 or > 6
            || !double.IsFinite(entry.ActiveSeconds) || entry.ActiveSeconds < 0)
            throw new InvalidDataException("JOURNAL_INVALID");
        return entry;
    }
    public void Clear() { File.Delete(path); File.Delete(path + ".tmp"); }
}

public sealed class ExperimentBudget
{
    public int Attempts { get; internal set; }
    public int Requests { get; private set; }
    public long Bytes { get; private set; }
    public double ActiveSeconds { get; internal set; }
    public int PageLoads { get; internal set; }
    public bool Exhausted => Attempts >= 12 || Requests >= 24 || Bytes >= 5 * 1024 * 1024 || ActiveSeconds >= 120 || PageLoads >= 6;
    public void Request()
    {
        if (Requests >= 24 || ActiveSeconds >= 120) throw new BudgetExceededException();
        Requests++;
    }
    public void AddBytes(int count)
    {
        if (count < 0 || Bytes + count > 5 * 1024 * 1024) throw new BudgetExceededException();
        Bytes += count;
    }
    public int RemainingBytes => (int)Math.Max(0, 5 * 1024 * 1024 - Bytes);
    public void Restore(JournalEntry entry)
    { Attempts = entry.Attempts; Requests = entry.Requests; Bytes = entry.Bytes; ActiveSeconds = entry.ActiveSeconds; PageLoads = entry.PageLoads; }
}
public sealed class BudgetExceededException : Exception { }
