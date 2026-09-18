using System.Collections.ObjectModel;
using DpiBypass.App.Infrastructure;
using DpiBypass.Core.Config;

namespace DpiBypass.App.ViewModels;

/// <summary>One entry in the appearance picker.</summary>
public sealed record AppearanceOption(AppearanceMode Mode, string Display, string Description)
{
    public override string ToString() => Display;
}

public sealed partial class MainViewModel
{
    private AppearanceOption _selectedAppearance = null!;
    private string _appearanceStatus = string.Empty;
    private string _appearanceStatusSeverity = string.Empty;
    private string _appearanceEvidence = string.Empty;

    /// <summary>
    /// Raised when the user picks a different window material.
    /// </summary>
    /// <remarks>
    /// An event rather than a direct call because the view model has no window: the app
    /// owns the window's lifetime, replaces it during recovery, and may be running with
    /// none at all while the engine works from the notification area.
    /// </remarks>
    public event Action<AppearanceMode>? AppearanceRequested;

    public ObservableCollection<AppearanceOption> AppearanceOptions { get; } =
    [
        new(AppearanceMode.System, "Sistem",
            "Windows'un önerdiği görünüm. Desteklenen sürümlerde Mica, diğerlerinde düz yüzey."),
        new(AppearanceMode.Mica, "Mica",
            "Pencere, masaüstü duvar kâğıdının renginden beslenen opak bir malzemeyle çizilir. Arkadaki pencereler görünmez."),
        new(AppearanceMode.Acrylic, "Acrylic cam",
            "Gerçek saydamlık: arkadaki pencereler bulanık biçimde görünür. Büyük yüzeyde Microsoft'un varsayılan önerisi değildir ve daha çok kaynak kullanır."),
        new(AppearanceMode.Plain, "Düz",
            "Tek renk yüzey. Her makinede aynı görünür ve en az kaynağı kullanır."),
    ];

    public AppearanceOption SelectedAppearance
    {
        get => _selectedAppearance;
        set
        {
            if (value is null || ReferenceEquals(_selectedAppearance, value))
            {
                return;
            }

            _selectedAppearance = value;
            Raise();

            if (_suppressPersist)
            {
                return;
            }

            _service.Settings.Appearance = value.Mode;

            // Kept in step so an older build reading this file still honours the choice,
            // and so the watchdog's own opt-out and the user's choice cannot disagree.
            _service.Settings.DisableWindowBackdrop = value.Mode == AppearanceMode.Plain;
            _service.SaveSettings();

            AppearanceRequested?.Invoke(value.Mode);
        }
    }

    /// <summary>What the window actually ended up with, in one sentence.</summary>
    public string AppearanceStatus
    {
        get => _appearanceStatus;
        private set => Set(ref _appearanceStatus, value);
    }

    /// <summary>"", "ok" or "warn" - the wording carries the meaning either way.</summary>
    public string AppearanceStatusSeverity
    {
        get => _appearanceStatusSeverity;
        private set => Set(ref _appearanceStatusSeverity, value);
    }

    /// <summary>
    /// Every input the decision was made from, for the diagnostics page.
    /// </summary>
    /// <remarks>
    /// "Mica is not showing" is not a report anybody can act on, and it is the report this
    /// app kept getting. The window either is or is not on the compositor, and the reason
    /// is a specific one out of about eight - the Windows build, composition being off,
    /// software rendering, a remote session, high contrast, the transparency switch, the
    /// energy saver, or DWM returning an HRESULT. Printing all of them means the next
    /// report says which.
    /// </remarks>
    public string AppearanceEvidence
    {
        get => _appearanceEvidence;
        private set => Set(ref _appearanceEvidence, value);
    }

    public bool ReduceMotion
    {
        get => _service.Settings.ReduceMotion;
        set
        {
            if (_service.Settings.ReduceMotion == value)
            {
                return;
            }

            _service.Settings.ReduceMotion = value;
            _service.SaveSettings();
            Raise();
            MotionPreferenceChanged?.Invoke(value);
        }
    }

    /// <summary>Raised when the user turns the app's own animations on or off.</summary>
    public event Action<bool>? MotionPreferenceChanged;

    /// <summary>Records what the window got, so the settings page can say so honestly.</summary>
    public void ReportAppearance(BackdropOutcome outcome)
    {
        AppearanceEvidence = WindowBackdrop.Explain();

        if (outcome.Applied)
        {
            AppearanceStatus = outcome.Reason;
            AppearanceStatusSeverity = "ok";
            return;
        }

        AppearanceStatus = outcome.Reason;

        // A downgrade is worth a warning colour because the user asked for something they
        // are not getting. A machine that simply has no backdrop to give, on the system
        // setting, is working exactly as intended and says so in plain text.
        AppearanceStatusSeverity = outcome.Downgraded ? "warn" : string.Empty;
    }

    private void InitialiseAppearance()
    {
        var mode = _service.Settings.Appearance;
        _selectedAppearance = AppearanceOptions.FirstOrDefault(option => option.Mode == mode)
            ?? AppearanceOptions[0];
    }
}
