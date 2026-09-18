using System.Windows;
using DpiBypass.App.Infrastructure;
using DpiBypass.Core;
using DpiBypass.Core.Connection;
using DpiBypass.Core.Logging;

namespace DpiBypass.App.ViewModels;

/// <summary>
/// The main screen's connection control: one state machine, and the properties the ring
/// and its label bind to.
/// </summary>
/// <remarks>
/// Every stage on screen corresponds to something the engine actually did. Nothing here
/// waits for an animation, nothing here invents a percentage, and nothing here is on a
/// timer: when the connection is quick the screen goes quickly, and a stage that this
/// network happens to skip is simply never shown.
/// </remarks>
public sealed partial class MainViewModel
{
    private readonly ConnectionFlow _connection = new();
    private CancellationTokenSource? _connectionCancellation;
    private ConnectionSnapshot _connectionSnapshot;

    /// <summary>Which stage the connection control is drawing.</summary>
    public ConnectionPhase ConnectionPhase => _connectionSnapshot.Phase;

    /// <summary>The headline under the ring: "Profiller deneniyor", "Bağlı", and so on.</summary>
    public string ConnectionTitle => _connectionSnapshot.Title;

    /// <summary>One line under the headline. Empty when there is nothing true to say.</summary>
    public string ConnectionDetail => _connectionSnapshot.Detail;

    /// <summary>True while the ring should be turning.</summary>
    public bool IsConnectionWorking => _connectionSnapshot.IsWorking;

    /// <summary>True when the attempt can still be called off.</summary>
    public bool CanCancelConnection => _connectionSnapshot.CanCancel;

    public bool IsConnected => _connectionSnapshot.IsConnected;

    public bool ConnectionFailed => _connectionSnapshot.IsFailed;

    /// <summary>The glyph in the middle of the ring, which changes with the stage.</summary>
    public string ConnectionSymbol => _connectionSnapshot.Phase switch
    {
        Core.Connection.ConnectionPhase.Connected => "Checkmark",
        Core.Connection.ConnectionPhase.Failed => "Warning",
        Core.Connection.ConnectionPhase.Cancelling => "Dismiss",
        Core.Connection.ConnectionPhase.Disconnecting => "Power",
        _ => "Power",
    };

    /// <summary>
    /// The button's own label. Says what pressing it does, not what state the app is in.
    /// </summary>
    public string ConnectionAction => _connectionSnapshot.Phase switch
    {
        Core.Connection.ConnectionPhase.Connected => "Bağlantıyı kes",
        Core.Connection.ConnectionPhase.Failed => "Yeniden dene",
        Core.Connection.ConnectionPhase.Cancelling => "İptal ediliyor…",
        Core.Connection.ConnectionPhase.Disconnecting => "Kesiliyor…",
        _ when _connectionSnapshot.IsWorking => "Bağlanıyor…",
        _ => "Bağlan",
    };

    /// <summary>
    /// Whether the app plays its own animations.
    /// </summary>
    /// <remarks>
    /// Windows' "show animations" preference wins outright; the app's own switch can only
    /// turn motion further down, never back on over the top of it. Bound rather than baked
    /// into a style so turning it off stops the ring immediately, on a window that is
    /// already open.
    /// </remarks>
    public bool MotionEnabled
    {
        get
        {
            try
            {
                return SystemParameters.ClientAreaAnimation && !_service.Settings.ReduceMotion;
            }
            catch (Exception)
            {
                return !_service.Settings.ReduceMotion;
            }
        }
    }

    /// <summary>Raised when a connection attempt ends without a working connection.</summary>
    public AsyncRelayCommand ConnectCommand { get; private set; } = null!;

    public RelayCommand CancelConnectionCommand { get; private set; } = null!;

    private void InitialiseConnection()
    {
        _connectionSnapshot = _connection.Current;
        _connection.Changed += OnConnectionChanged;

        ConnectCommand = new AsyncRelayCommand(
            ToggleAsync,
            () => !_connectionSnapshot.IsWorking);

        CancelConnectionCommand = new RelayCommand(
            CancelConnection,
            () => _connectionSnapshot.CanCancel);
    }

    private void OnConnectionChanged(ConnectionSnapshot snapshot)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnConnectionChanged(snapshot));
            return;
        }

        _connectionSnapshot = snapshot;

        Raise(nameof(ConnectionPhase));
        Raise(nameof(ConnectionTitle));
        Raise(nameof(ConnectionDetail));
        Raise(nameof(IsConnectionWorking));
        Raise(nameof(CanCancelConnection));
        Raise(nameof(IsConnected));
        Raise(nameof(ConnectionFailed));
        Raise(nameof(ConnectionSymbol));
        Raise(nameof(ConnectionAction));

        ConnectCommand.RaiseCanExecuteChanged();
        CancelConnectionCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Stops the attempt on screen. The work behind it unwinds at its own pace.</summary>
    private void CancelConnection()
    {
        if (!_connection.TryCancel(out _))
        {
            return;
        }

        try
        {
            _connectionCancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already finished; the screen has moved on either way.
        }
    }

    /// <summary>
    /// Maps what the engine says into the stage the screen is showing.
    /// </summary>
    /// <remarks>
    /// Called from the service's own change notification, so it covers the connection
    /// coming up or dropping without anybody pressing anything - protection started at
    /// launch, the filter released and reopened, a network change re-tuning underneath.
    /// Those adopt rather than report: there is no attempt of ours for them to belong to,
    /// and the screen has to follow the engine rather than argue with it.
    /// </remarks>
    private void SyncConnectionWithService()
    {
        var generation = _connectionSnapshot.Generation;

        switch (_service.State)
        {
            case ProtectionState.Running when _service.IsFilterDown:
                _connection.Report(
                    generation,
                    Core.Connection.ConnectionPhase.Verifying,
                    "Paket süzgeci yeniden açılıyor",
                    _service.StatusDetail ?? string.Empty);
                break;

            case ProtectionState.Running:
                if (!_connection.Report(generation, Core.Connection.ConnectionPhase.Connected, _service.Strategy.Name))
                {
                    _connection.Adopt(Core.Connection.ConnectionPhase.Connected, _service.Strategy.Name);
                }

                break;

            case ProtectionState.Degraded:
                // Protection is running; the site still does not answer. Saying "connected"
                // over that would be the one thing this screen must never do, and saying
                // nothing leaves the user looking at a spinner that has stopped.
                if (!_connection.Report(
                        generation,
                        Core.Connection.ConnectionPhase.Failed,
                        "Çalışan bir yöntem bulunamadı",
                        _service.StatusDetail ?? "Bu ağda denenen profillerin hiçbiri hedefe ulaşamadı."))
                {
                    _connection.Adopt(Core.Connection.ConnectionPhase.Failed, _service.StatusDetail ?? string.Empty);
                }

                break;

            case ProtectionState.Starting:
                if (!_connection.Report(
                        generation,
                        Core.Connection.ConnectionPhase.CheckingNetwork,
                        _service.StatusDetail ?? string.Empty))
                {
                    if (!_connectionSnapshot.IsWorking)
                    {
                        _connection.Adopt(Core.Connection.ConnectionPhase.CheckingNetwork, _service.StatusDetail ?? string.Empty);
                    }
                }

                break;

            case ProtectionState.Stopping:
                if (!_connectionSnapshot.IsWorking)
                {
                    _connection.Adopt(Core.Connection.ConnectionPhase.Disconnecting, _service.StatusDetail ?? string.Empty);
                }

                break;

            case ProtectionState.Stopped:
                if (_connectionSnapshot.Phase is not (Core.Connection.ConnectionPhase.Ready or Core.Connection.ConnectionPhase.Failed))
                {
                    _connection.Adopt(Core.Connection.ConnectionPhase.Ready, _service.StatusDetail ?? string.Empty);
                }

                break;
        }
    }

    /// <summary>The candidate sweep's own progress, which is the one stage with a counter.</summary>
    private void ReportProfileTrial(string name, int index, int total)
        => _connection.Report(
            _connectionSnapshot.Generation,
            Core.Connection.ConnectionPhase.TryingProfiles,
            $"{index}/{total} · {name}");

    /// <summary>
    /// Connects or disconnects, driving the flow.
    /// </summary>
    /// <remarks>
    /// The flow is entered before any work starts, so the screen reacts to the press
    /// rather than to the first thing the engine reports - and the guard inside it, not a
    /// flag here, is what makes a second press do nothing.
    /// </remarks>
    public async Task ToggleAsync()
    {
        var disconnecting = _connectionSnapshot.IsConnected || _isRunning;

        if (disconnecting)
        {
            if (!_connection.TryBeginDisconnect(out var stopping))
            {
                _connection.Adopt(Core.Connection.ConnectionPhase.Disconnecting);
                stopping = _connection.Current.Generation;
            }

            IsBusy = true;
            try
            {
                await _service.StopAsync().ConfigureAwait(true);
                _connection.Settle(stopping);
            }
            catch (Exception ex)
            {
                AppLog.Error("Koruma durdurulamadı", ex);
                StatusHeadline = "Tam olarak durdurulamadı";
                StatusDetail = ex.Message;
                _connection.Report(stopping, Core.Connection.ConnectionPhase.Failed, "Tam olarak durdurulamadı", ex.Message);
            }
            finally
            {
                IsBusy = false;
            }

            return;
        }

        if (!_connection.TryBeginConnect(out var generation))
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _connectionCancellation, cancellation);
        previous?.Dispose();

        IsBusy = true;
        try
        {
            await _service.StartAsync(cancellation.Token).ConfigureAwait(true);

            // What the engine settled on decides the ending, not the fact that StartAsync
            // returned: it returns just as happily on a network where nothing got through.
            SyncConnectionWithService();
        }
        catch (OperationCanceledException)
        {
            _connection.Settle(_connection.Current.Generation, "İptal edildi.");
        }
        catch (Exception ex)
        {
            AppLog.Error("Koruma başlatılamadı", ex);
            StatusHeadline = "Başlatılamadı";
            StatusDetail = ex.Message;
            _connection.Report(generation, Core.Connection.ConnectionPhase.Failed, "Başlatılamadı", ex.Message);
        }
        finally
        {
            IsBusy = false;

            if (ReferenceEquals(Volatile.Read(ref _connectionCancellation), cancellation))
            {
                Interlocked.Exchange(ref _connectionCancellation, null)?.Dispose();
            }
        }
    }

    private void DisposeConnection()
    {
        _connection.Changed -= OnConnectionChanged;

        try
        {
            Interlocked.Exchange(ref _connectionCancellation, null)?.Dispose();
        }
        catch (Exception)
        {
            // Shutdown.
        }
    }
}
