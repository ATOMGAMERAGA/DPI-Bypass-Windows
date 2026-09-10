using System.Xml.Linq;
using DpiBypass.Core.Network;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// What the app is allowed to keep doing, and keep holding, while nobody is looking at it.
/// </summary>
/// <remarks>
/// Every one of these is a regression somebody has to be able to see coming. A watch
/// started twice, a timer that ticks for a window with no pixels, an unbounded queue fed
/// from the packet path and a model left loaded between runs all behave perfectly - they
/// simply cost the user memory and a core for nothing, which is exactly the kind of
/// change that gets reintroduced because nothing failed when it was.
/// </remarks>
public sealed class BackgroundFootprintTests
{
    private static string AppSource(string fileName)
        => File.ReadAllText(RepoFiles.Find("src", "DpiBypass.App", fileName));

    private static string ViewModel() => File.ReadAllText(RepoFiles.MainViewModel);

    // ---- the shared network watch ------------------------------------------------

    [Fact]
    public void SharedWatchReportsTheUnderlyingNetwork()
    {
        var inner = new FakeNetworkWatch { Current = new NetworkFingerprint { Ssid = "atom" } };
        using var shared = new SharedNetworkWatch(inner);

        Assert.Equal("atom", shared.Current.Ssid);
    }

    [Fact]
    public void SharedWatchNeverStartsASecondPoll()
    {
        var inner = new FakeNetworkWatch();
        using var shared = new SharedNetworkWatch(inner);

        shared.Start();
        shared.Start();

        Assert.Equal(0, inner.Starts);
    }

    [Fact]
    public void SharedWatchForwardsChangesToItsOwnSubscribers()
    {
        var inner = new FakeNetworkWatch();
        using var shared = new SharedNetworkWatch(inner);

        NetworkFingerprint? seen = null;
        shared.Changed += network => seen = network;

        inner.Raise(new NetworkFingerprint { Ssid = "vodafone" });

        Assert.Equal("vodafone", seen?.Ssid);
    }

    [Fact]
    public void SharedWatchOnlySubscribesToTheInnerWatchWhileItHasSubscribers()
    {
        var inner = new FakeNetworkWatch();
        using var shared = new SharedNetworkWatch(inner);

        Assert.Equal(0, inner.Subscribers);

        void Handler(NetworkFingerprint _)
        {
        }

        shared.Changed += Handler;
        Assert.Equal(1, inner.Subscribers);

        shared.Changed -= Handler;
        Assert.Equal(0, inner.Subscribers);
    }

    /// <summary>
    /// Disposing the view must leave the watch itself running for everybody else.
    /// </summary>
    /// <remarks>
    /// The latency lane disposes its watch when low latency mode is switched off. That
    /// watch is now the protection service's, and the Vodafone card reads the network
    /// name from it whether or not protection - or low latency mode - is running.
    /// </remarks>
    [Fact]
    public void DisposingTheSharedViewLeavesTheUnderlyingWatchAlone()
    {
        var inner = new FakeNetworkWatch();
        var shared = new SharedNetworkWatch(inner);

        var seen = 0;
        shared.Changed += _ => seen++;

        shared.Dispose();
        inner.Raise(new NetworkFingerprint());

        Assert.False(inner.Disposed);
        Assert.Equal(0, seen);
        Assert.Equal(0, inner.Subscribers);
    }

    // ---- the OCR engine ----------------------------------------------------------

    /// <summary>Releasing an engine that was never built is a no-op, not a failure.</summary>
    [Fact]
    public void ReleasingIdleOcrResourcesIsSafeBeforeAndAfterDisposal()
    {
        var source = new ValorantHudLatencySource();

        source.ReleaseIdleResources();
        source.Dispose();
        source.ReleaseIdleResources();
    }

    [Fact]
    public void EveryLatencyRunHandsBackWhatItWasHoldingOnlyForItself()
    {
        var service = File.ReadAllText(RepoFiles.Find("src", "DpiBypass.Core", "ProtectionService.cs"));

        Assert.Contains("ReleaseIdleLatencyResources()", service, StringComparison.Ordinal);
        Assert.Contains("_valorantLatency?.ReleaseIdleResources()", service, StringComparison.Ordinal);
    }

    // ---- the idle window ---------------------------------------------------------

    /// <summary>
    /// A window nobody can see must not be re-formatting counters on a timer.
    /// </summary>
    [Fact]
    public void PresentationTimerStopsRatherThanSlowingDown()
    {
        var source = ViewModel();

        Assert.Contains("_refreshTimer.Stop();", source, StringComparison.Ordinal);
        Assert.DoesNotContain("HiddenRefreshInterval", source, StringComparison.Ordinal);
    }

    /// <summary>
    /// A launch that goes straight to the notification area never shows the window, so
    /// nothing would ever have told the view model that nobody is looking.
    /// </summary>
    [Fact]
    public void TheViewModelStartsIdleUntilTheWindowIsActuallyShown()
        => Assert.Contains(
            "_viewModel.SetPresentationActive(false);",
            AppSource("MainWindow.xaml.cs"),
            StringComparison.Ordinal);

    /// <summary>A minimised WPF window keeps IsVisible true, so it needs asking about too.</summary>
    [Fact]
    public void MinimisingCountsAsBeingOutOfSight()
    {
        var window = AppSource("MainWindow.xaml.cs");

        Assert.Contains("protected override void OnStateChanged", window, StringComparison.Ordinal);
        Assert.Contains("IsVisible && WindowState != WindowState.Minimized", window, StringComparison.Ordinal);
    }

    /// <summary>Both display queues are bounded and both stop posting while nobody looks.</summary>
    [Fact]
    public void DisplayQueuesAreBoundedAndIdleWhileTheWindowIsAway()
    {
        var source = ViewModel();

        Assert.Contains("PendingHostCapacity", source, StringComparison.Ordinal);
        Assert.Contains("Interlocked.Increment(ref _pendingHostCount) > PendingHostCapacity", source, StringComparison.Ordinal);

        // Both drain schedulers ask the same question before waking the dispatcher.
        Assert.Equal(2, Occurrences(source, "if (!IsPresentationActive)"));
    }

    /// <summary>The memory a finished startup or a finished run is no longer using goes back.</summary>
    [Fact]
    public void TheAppHandsMemoryBackOnceTheWindowHasBeenAway()
    {
        var app = AppSource("App.xaml.cs");

        Assert.Contains("ProcessMemory.TrimWorkingSet()", app, StringComparison.Ordinal);
        Assert.Contains("GCCollectionMode.Optimized", app, StringComparison.Ordinal);

        // Cancelled by coming back, so minimise-and-restore is not a trim followed by
        // faulting the same pages straight back in.
        Assert.Contains("_idleMemoryTimer?.Stop();", app, StringComparison.Ordinal);
    }

    // ---- the published runtime ---------------------------------------------------

    /// <summary>
    /// Server GC would reserve a heap and a thread per core for a desktop app.
    /// </summary>
    [Fact]
    public void ThePublishedAppUsesWorkstationGarbageCollection()
    {
        var project = XDocument.Load(RepoFiles.Find("src", "DpiBypass.App", "DpiBypass.App.csproj"));

        Assert.Equal("false", Property(project, "ServerGarbageCollection"));
        Assert.Equal("false", Property(project, "RetainVMGarbageCollection"));

        // Left on deliberately: the packet path pays for a blocking gen2 pause.
        Assert.Equal("true", Property(project, "ConcurrentGarbageCollection"));
    }

    private static string? Property(XDocument project, string name) => project
        .Descendants()
        .FirstOrDefault(element => element.Name.LocalName == name)
        ?.Value;

    private static int Occurrences(string text, string value)
    {
        var count = 0;
        for (var index = text.IndexOf(value, StringComparison.Ordinal);
             index >= 0;
             index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }

    private sealed class FakeNetworkWatch : INetworkWatch
    {
        private Action<NetworkFingerprint>? _changed;

        public NetworkFingerprint Current { get; set; } = new();

        public int Starts { get; private set; }

        public int Subscribers { get; private set; }

        public bool Disposed { get; private set; }

        public event Action<NetworkFingerprint>? Changed
        {
            add
            {
                _changed += value;
                Subscribers++;
            }

            remove
            {
                _changed -= value;
                Subscribers--;
            }
        }

        public void Start() => Starts++;

        public void Raise(NetworkFingerprint network) => _changed?.Invoke(network);

        public void Dispose() => Disposed = true;
    }
}
