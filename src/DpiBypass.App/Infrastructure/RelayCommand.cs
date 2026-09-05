using System.Windows.Input;

namespace DpiBypass.App.Infrastructure;

public sealed class RelayCommand : ICommand
{
    private readonly Action _execute;
    private readonly Func<bool>? _canExecute;

    public RelayCommand(Action execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => _canExecute?.Invoke() ?? true;

    public void Execute(object? parameter) => _execute();

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Async command that refuses to run twice at once. Every button in this app kicks
/// off network work, so re-entrancy would mean two tuning sweeps fighting over the
/// engine's active strategy.
/// </summary>
public sealed class AsyncRelayCommand : ICommand
{
    private readonly Func<Task> _execute;
    private readonly Func<bool>? _canExecute;
    private bool _running;

    public AsyncRelayCommand(Func<Task> execute, Func<bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !_running && (_canExecute?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        // CanExecute is checked here as well as by the button, because not every caller
        // is a button: the notification area menu invokes these directly, and "start
        // protection" from the tray while a start is already under way is the same
        // no-op that made the toolbar button look broken.
        if (_running || !CanExecute(parameter))
        {
            return;
        }

        _running = true;
        RaiseCanExecuteChanged();

        try
        {
            await _execute().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            // The user asked for this, or the app is closing. Colouring somebody's own
            // decision red - and filing it in the log next to real failures - is how a
            // cancel button ends up looking like something went wrong.
            DpiBypass.Core.Logging.AppLog.Info("İşlem iptal edildi.");
        }
        catch (Exception ex)
        {
            DpiBypass.Core.Logging.AppLog.Error("Komut başarısız", ex);
        }
        finally
        {
            _running = false;
            RaiseCanExecuteChanged();
        }
    }

    public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
