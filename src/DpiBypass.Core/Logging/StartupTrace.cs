using System.Diagnostics;
using System.Text;

namespace DpiBypass.Core.Logging;

/// <summary>
/// A timestamped record of how far startup got, kept in memory and written out only
/// when something goes wrong.
/// </summary>
/// <remarks>
/// <para>
/// The failure this app has been bitten by is a process that starts, stays alive, and
/// never puts a usable window on screen - and the log said nothing, because every step
/// on the way succeeded. What was missing was not more logging but ordering: which
/// step was last, how long it took, and on which thread. That is all this keeps.
/// </para>
/// <para>
/// It is quiet by default. Every mark also goes to the log at debug level, which is
/// below the normal threshold, so a healthy start adds nothing to the file. When the
/// window turns out not to be reachable the whole timeline is written out at once, so
/// the one log anybody ever sends contains the evidence rather than a summary of it.
/// </para>
/// </remarks>
public static class StartupTrace
{
    /// <summary>Enough for the whole startup path several times over.</summary>
    private const int Capacity = 160;

    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly Lock Gate = new();
    private static readonly List<string> Marks = new(Capacity);

    private static bool _dumped;

    /// <summary>Records one startup milestone with its elapsed time and thread.</summary>
    public static void Mark(string milestone)
    {
        var line = $"+{Clock.Elapsed.TotalMilliseconds,8:0.0} ms · T{Environment.CurrentManagedThreadId,-3} · {milestone}";

        lock (Gate)
        {
            if (Marks.Count < Capacity)
            {
                Marks.Add(line);
            }
            else if (Marks.Count == Capacity)
            {
                Marks.Add("… (izleme sınırı doldu)");
            }
        }

        AppLog.Debug(line);
    }

    /// <summary>Everything recorded so far, oldest first.</summary>
    public static IReadOnlyList<string> Timeline
    {
        get
        {
            lock (Gate)
            {
                return [.. Marks];
            }
        }
    }

    /// <summary>
    /// Writes the whole timeline to the log. Used once, when the window could not be
    /// shown, because that is the only time anybody needs it.
    /// </summary>
    public static void Dump(string reason)
    {
        lock (Gate)
        {
            if (_dumped)
            {
                return;
            }

            _dumped = true;
        }

        var report = new StringBuilder();
        report.Append("Açılış izlemesi (").Append(reason).AppendLine("):");

        foreach (var mark in Timeline)
        {
            report.Append("    ").AppendLine(mark);
        }

        AppLog.Warning(report.ToString().TrimEnd());
    }

    /// <summary>Test seam: forgets everything recorded so far.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            Marks.Clear();
            _dumped = false;
        }
    }
}
