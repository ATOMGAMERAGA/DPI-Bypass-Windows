using System.Globalization;

namespace DpiBypass.Core.Network;

/// <summary>
/// Every step of the deep test, as a state the user can be shown one at a time.
/// </summary>
/// <remarks>
/// The deep test asks the user to start and stop transfers, and it needs more than one of
/// them: the whole point of the Traffic Guard half is that a QoS policy only attaches to
/// transport endpoints created after it exists, so the transfer measured before the
/// policy cannot be the transfer measured after it. An earlier build asked for one upload
/// in a single static line of text and then silently waited for two more. This enum is
/// the fix: every wait the run performs is a state with a name, and the card shows the
/// one it is actually in.
/// </remarks>
public enum LoadedLaneStage
{
    Idle = 0,

    /// <summary>Resolving and pinning the endpoint every later number belongs to.</summary>
    VerifyingTarget = 1,

    /// <summary>Waiting for the user's own traffic to stop so a baseline is possible.</summary>
    WaitingForQuietLink = 2,

    IdleBaseline = 3,

    /// <summary>Asking the user to start the upload the baseline will be measured against.</summary>
    AwaitingUploadStart = 4,

    MeasuringUploadBaseline = 5,

    /// <summary>Asking the user to stop, so the policy is created on a quiet link.</summary>
    AwaitingUploadStop = 6,

    ApplyingPolicy = 7,

    /// <summary>Asking for a new transfer, because the policy only matches new endpoints.</summary>
    AwaitingFreshUpload = 8,

    MeasuringUploadCandidate = 9,

    AwaitingDownloadStart = 10,

    MeasuringDownload = 11,

    /// <summary>The independent round that decides, separate from the search that chose.</summary>
    Confirming = 12,

    Committed = 13,

    NoGain = 14,

    RolledBack = 15,

    Cancelled = 16,

    Failed = 17,
}

/// <summary>What the card shows while one stage is running.</summary>
/// <remarks>
/// Everything here is measured or counted, never estimated. A rate the user cannot see on
/// their own transfer would be worse than no rate at all.
/// </remarks>
public sealed record LoadedLaneProgress
{
    public static readonly LoadedLaneProgress Off = new()
    {
        Stage = LoadedLaneStage.Idle,
        Title = "Kapalı",
        Instruction = string.Empty,
    };

    public required LoadedLaneStage Stage { get; init; }

    /// <summary>The state's own name, for the status line.</summary>
    public required string Title { get; init; }

    /// <summary>What the user has to do right now, if anything.</summary>
    public required string Instruction { get; init; }

    /// <summary>Which direction this stage is about, when it is about one.</summary>
    public LoadDirection? Direction { get; init; }

    public string Target { get; init; } = string.Empty;

    /// <summary>The rate the adapter counters show right now, in kbit/s.</summary>
    public double? InstantKbps { get; init; }

    /// <summary>How close that is to the measured capacity, when capacity is known.</summary>
    public double? CapacityShare { get; init; }

    public LinkLoadClassification Load { get; init; } = LinkLoadClassification.Unknown;

    /// <summary>Time left in this stage before it gives up.</summary>
    public TimeSpan? Remaining { get; init; }

    /// <summary>Bytes the link has carried since the run started, in both directions.</summary>
    public long DataUsedBytes { get; init; }

    /// <summary>Whether the user can stop the run from where it is.</summary>
    public bool CanCancel { get; init; } = true;

    /// <summary>Why the stage ended, when it ended for a reason worth showing.</summary>
    public string? Outcome { get; init; }

    public bool IsWaitingOnUser => Stage is LoadedLaneStage.AwaitingUploadStart
        or LoadedLaneStage.AwaitingUploadStop
        or LoadedLaneStage.AwaitingFreshUpload
        or LoadedLaneStage.AwaitingDownloadStart
        or LoadedLaneStage.WaitingForQuietLink;

    public bool IsTerminal => Stage is LoadedLaneStage.Committed
        or LoadedLaneStage.NoGain
        or LoadedLaneStage.RolledBack
        or LoadedLaneStage.Cancelled
        or LoadedLaneStage.Failed;

    /// <summary>The one line the card puts under the stage title.</summary>
    public string DescribeRate()
    {
        if (InstantKbps is not { } kbps)
        {
            return string.Empty;
        }

        var rate = string.Create(CultureInfo.CurrentCulture, $"{kbps / 1000:F1} Mbit/s");
        return CapacityShare is { } share
            ? string.Create(CultureInfo.CurrentCulture, $"{rate} · kapasitenin %{share * 100:F0}'i")
            : rate;
    }

    /// <summary>Data used so far, in the unit that makes it readable.</summary>
    public string DescribeData() => DataUsedBytes switch
    {
        < 1024 => string.Create(CultureInfo.CurrentCulture, $"{DataUsedBytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{DataUsedBytes / 1024d:F0} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.CurrentCulture, $"{DataUsedBytes / (1024d * 1024):F1} MB"),
        _ => string.Create(CultureInfo.CurrentCulture, $"{DataUsedBytes / (1024d * 1024 * 1024):F2} GB"),
    };

    /// <summary>The Turkish label for one stage, used as the default title.</summary>
    public static string TitleFor(LoadedLaneStage stage) => stage switch
    {
        LoadedLaneStage.Idle => "Kapalı",
        LoadedLaneStage.VerifyingTarget => "Hedef doğrulanıyor",
        LoadedLaneStage.WaitingForQuietLink => "Hattın boşalması bekleniyor",
        LoadedLaneStage.IdleBaseline => "Boştaki değer ölçülüyor",
        LoadedLaneStage.AwaitingUploadStart => "Upload başlatın",
        LoadedLaneStage.MeasuringUploadBaseline => "Upload ölçülüyor",
        LoadedLaneStage.AwaitingUploadStop => "Upload'u durdurun",
        LoadedLaneStage.ApplyingPolicy => "Policy uygulanıyor",
        LoadedLaneStage.AwaitingFreshUpload => "Yeni upload bağlantısı bekleniyor",
        LoadedLaneStage.MeasuringUploadCandidate => "Candidate ölçülüyor",
        LoadedLaneStage.AwaitingDownloadStart => "İndirme başlatın",
        LoadedLaneStage.MeasuringDownload => "Download ölçülüyor",
        LoadedLaneStage.Confirming => "Doğrulanıyor",
        LoadedLaneStage.Committed => "Kazanç uygulandı",
        LoadedLaneStage.NoGain => "Kazanç bulunamadı",
        LoadedLaneStage.RolledBack => "Geri alındı",
        LoadedLaneStage.Cancelled => "İptal edildi",
        _ => "Başarısız",
    };
}

/// <summary>Where a running deep test publishes the state the card renders.</summary>
public interface ILatencyStageReporter
{
    void Report(LoadedLaneProgress progress);
}

/// <summary>Adapts a plain callback, which is all the service needs to supply.</summary>
public sealed class DelegateStageReporter : ILatencyStageReporter
{
    private readonly Action<LoadedLaneProgress> _report;

    public DelegateStageReporter(Action<LoadedLaneProgress> report)
        => _report = report ?? throw new ArgumentNullException(nameof(report));

    public void Report(LoadedLaneProgress progress) => _report(progress);
}

/// <summary>Swallows everything, for callers that do not draw a card.</summary>
public sealed class NullStageReporter : ILatencyStageReporter
{
    public static readonly NullStageReporter Instance = new();

    public void Report(LoadedLaneProgress progress)
    {
    }
}
