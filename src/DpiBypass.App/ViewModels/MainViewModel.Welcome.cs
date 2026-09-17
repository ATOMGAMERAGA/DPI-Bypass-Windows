using System.Collections.ObjectModel;
using System.Windows.Threading;
using DpiBypass.App.Infrastructure;
using DpiBypass.Core;
using DpiBypass.Core.Logging;
using DpiBypass.Core.Onboarding;

namespace DpiBypass.App.ViewModels;

/// <summary>
/// The greeting that fills the window's content area before the app itself.
/// </summary>
/// <remarks>
/// <para>
/// It lives inside the window. It does not go full screen, it does not maximise anything,
/// and it does not touch the window's size or position - the title bar and the close,
/// minimise and maximise buttons stay exactly where they were and keep working. What it
/// covers is the rail and the page, which is the part of the window the app's own content
/// would otherwise be in.
/// </para>
/// <para>
/// Whether it appears at all is <see cref="WelcomeFlow"/>'s decision, and it is a decision
/// about arrivals: a shortcut double-clicked is one, the logon task starting the app into
/// the notification area is not, and a window coming back from the tray is not either.
/// </para>
/// </remarks>
/// <summary>One dot in the introduction's progress row.</summary>
/// <remarks>
/// A tiny observable rather than a plain record: the row is bound once and the dots light
/// and dim in place, so moving between cards does not rebuild four elements and re-run
/// their entrance.
/// </remarks>
public sealed class WelcomeStep : ObservableObject
{
    private bool _isCurrent;

    public required int Ordinal { get; init; }

    public bool IsCurrent
    {
        get => _isCurrent;
        set => Set(ref _isCurrent, value);
    }
}

public sealed partial class MainViewModel
{
    /// <summary>
    /// How long the short greeting stays before the app appears.
    /// </summary>
    /// <remarks>
    /// Long enough to read four words, short enough that somebody opening the app for the
    /// fifth time today does not feel held up. Nothing waits on it: the engine has already
    /// been told to start, and pressing "Başla" ends it immediately.
    /// </remarks>
    private static readonly TimeSpan BriefWelcomeDwell = TimeSpan.FromSeconds(1.6);

    private WelcomeKind _welcomeKind = WelcomeKind.None;
    private int _welcomeIndex;
    private DispatcherTimer? _briefWelcomeTimer;

    /// <summary>Raised when the greeting finishes, so the window can fade the app in.</summary>
    public event Action? WelcomeFinished;

    public ObservableCollection<WelcomeCard> WelcomeCards { get; } = [.. WelcomeFlow.Cards];

    /// <summary>The progress dots, one per card.</summary>
    public ObservableCollection<WelcomeStep> WelcomeSteps { get; } =
        [.. WelcomeFlow.Cards.Select((_, ordinal) => new WelcomeStep { Ordinal = ordinal, IsCurrent = ordinal == 0 })];

    /// <summary>True while the greeting owns the content area.</summary>
    public bool IsWelcomeVisible => _welcomeKind != WelcomeKind.None;

    /// <summary>True for the four-card introduction; false for the one-line greeting.</summary>
    public bool IsWelcomeTour => _welcomeKind == WelcomeKind.Tour;

    public string WelcomeTitle => IsWelcomeTour
        ? WelcomeCards[_welcomeIndex].Title
        : $"{AppPaths.ProductName}";

    public string WelcomeBody => IsWelcomeTour
        ? WelcomeCards[_welcomeIndex].Body
        : WelcomeFlow.BriefLine;

    public string WelcomeSymbol => IsWelcomeTour
        ? WelcomeCards[_welcomeIndex].Symbol
        : "Sparkle";

    /// <summary>"1 / 4", for the dots' accessible name.</summary>
    public string WelcomeProgress => IsWelcomeTour
        ? $"{_welcomeIndex + 1} / {WelcomeCards.Count}"
        : string.Empty;

    public int WelcomeIndex => _welcomeIndex;

    public bool CanGoBackInWelcome => IsWelcomeTour && _welcomeIndex > 0;

    public bool IsLastWelcomeCard => !IsWelcomeTour || _welcomeIndex >= WelcomeCards.Count - 1;

    /// <summary>The primary button: "İleri" until the last card, then "Başla".</summary>
    public string WelcomePrimaryAction => IsLastWelcomeCard ? "Başla" : "İleri";

    /// <summary>The "Açılışta göster" switch, shown on the greeting and in Settings.</summary>
    public bool ShowWelcomeOnStartup
    {
        get => _service.Settings.ShowWelcomeOnStartup;
        set
        {
            if (_service.Settings.ShowWelcomeOnStartup == value)
            {
                return;
            }

            _service.Settings.ShowWelcomeOnStartup = value;
            _service.SaveSettings();
            Raise();
        }
    }

    public RelayCommand WelcomeNextCommand { get; private set; } = null!;

    public RelayCommand WelcomeBackCommand { get; private set; } = null!;

    public RelayCommand WelcomeSkipCommand { get; private set; } = null!;

    /// <summary>Re-opens the introduction from Settings, whatever the startup preference says.</summary>
    public RelayCommand ShowWelcomeTourCommand { get; private set; } = null!;

    private void InitialiseWelcome()
    {
        WelcomeNextCommand = new RelayCommand(AdvanceWelcome, () => IsWelcomeVisible);
        WelcomeBackCommand = new RelayCommand(GoBackInWelcome, () => CanGoBackInWelcome);
        WelcomeSkipCommand = new RelayCommand(() => FinishWelcome(completed: true), () => IsWelcomeVisible);
        ShowWelcomeTourCommand = new RelayCommand(() => BeginWelcome(WelcomeKind.Tour));
    }

    /// <summary>Starts the greeting the launch context calls for. None means go straight in.</summary>
    public void BeginWelcome(WelcomeKind kind)
    {
        if (kind == WelcomeKind.None)
        {
            return;
        }

        _welcomeKind = kind;
        _welcomeIndex = 0;
        RaiseWelcome();

        if (kind == WelcomeKind.Brief)
        {
            StartBriefWelcomeTimer();
        }
    }

    /// <summary>
    /// Ends the short greeting on its own after a moment.
    /// </summary>
    /// <remarks>
    /// A timer on the dispatcher rather than an awaited delay, so nothing holds a task
    /// alive across a window that may be closed and rebuilt underneath it. It is stopped
    /// the instant the greeting ends by any other route.
    /// </remarks>
    private void StartBriefWelcomeTimer()
    {
        StopBriefWelcomeTimer();

        _briefWelcomeTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = BriefWelcomeDwell,
        };

        _briefWelcomeTimer.Tick += (_, _) =>
        {
            StopBriefWelcomeTimer();
            FinishWelcome(completed: false);
        };

        _briefWelcomeTimer.Start();
    }

    private void StopBriefWelcomeTimer()
    {
        var timer = _briefWelcomeTimer;
        _briefWelcomeTimer = null;
        timer?.Stop();
    }

    private void AdvanceWelcome()
    {
        if (!IsWelcomeVisible)
        {
            return;
        }

        if (IsLastWelcomeCard)
        {
            FinishWelcome(completed: true);
            return;
        }

        _welcomeIndex++;
        RaiseWelcome();
    }

    private void GoBackInWelcome()
    {
        if (!CanGoBackInWelcome)
        {
            return;
        }

        _welcomeIndex--;
        RaiseWelcome();
    }

    /// <summary>
    /// Ends the greeting and hands the window to the app.
    /// </summary>
    /// <param name="completed">
    /// True for any way out the user chose - finishing, skipping, or dismissing the brief
    /// greeting. Skipping counts: somebody who pressed "Atla" has decided they do not want
    /// the introduction, and showing it again on the next launch would be answering their
    /// decision with a repeat of the question.
    /// </param>
    private void FinishWelcome(bool completed)
    {
        if (!IsWelcomeVisible)
        {
            return;
        }

        StopBriefWelcomeTimer();

        var wasTour = _welcomeKind == WelcomeKind.Tour;
        _welcomeKind = WelcomeKind.None;
        RaiseWelcome();

        if (completed && wasTour && !_service.Settings.WelcomeCompleted)
        {
            try
            {
                _service.Settings.WelcomeCompleted = true;
                _service.SaveSettings();
            }
            catch (Exception ex)
            {
                // Showing the introduction once more is a small cost; failing to start is not.
                AppLog.Error("Tanıtım tercihi kaydedilemedi", ex);
            }
        }

        WelcomeFinished?.Invoke();
    }

    private void RaiseWelcome()
    {
        foreach (var step in WelcomeSteps)
        {
            step.IsCurrent = step.Ordinal == _welcomeIndex;
        }

        Raise(nameof(IsWelcomeVisible));
        Raise(nameof(IsWelcomeTour));
        Raise(nameof(WelcomeTitle));
        Raise(nameof(WelcomeBody));
        Raise(nameof(WelcomeSymbol));
        Raise(nameof(WelcomeProgress));
        Raise(nameof(WelcomeIndex));
        Raise(nameof(CanGoBackInWelcome));
        Raise(nameof(IsLastWelcomeCard));
        Raise(nameof(WelcomePrimaryAction));

        WelcomeNextCommand.RaiseCanExecuteChanged();
        WelcomeBackCommand.RaiseCanExecuteChanged();
        WelcomeSkipCommand.RaiseCanExecuteChanged();
    }
}
