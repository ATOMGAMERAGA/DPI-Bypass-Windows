using DpiBypass.Core.Interop;
using Xunit;

namespace DpiBypass.Tests;

public sealed class WlanMediaStreamingSessionTests
{
    [Fact]
    public void SettingRemainsActiveAfterApplyReturnsUntilExplicitRestore()
    {
        var native = new Native();
        var sessions = native.Sessions();
        var adapter = Guid.NewGuid();

        Assert.True(sessions.TrySet(adapter, true));
        Assert.True(native.Query(adapter));
        Assert.Empty(native.Closed);
        Assert.True(sessions.TrySet(adapter, false));
        Assert.False(native.Query(adapter));
        Assert.Single(native.Closed);
    }

    [Fact]
    public void ReapplyReusesTheClientAndReassertsAfterAReconnect()
    {
        var native = new Native();
        var sessions = native.Sessions();
        var adapter = Guid.NewGuid();
        Assert.True(sessions.TrySet(adapter, true));
        native.Requests.Clear(); // Windows clears the request when the interface disconnects.
        Assert.True(sessions.TrySet(adapter, true));
        Assert.Equal(1, native.Opened);
        Assert.True(native.Query(adapter));
        Assert.True(sessions.TrySet(adapter, false));
    }

    [Fact]
    public void RestoringOneAdapterDoesNotReleaseAnotherOrAnotherApplicationsRequest()
    {
        var native = new Native();
        var sessions = native.Sessions();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        Assert.True(sessions.TrySet(first, true));
        Assert.True(sessions.TrySet(second, true));
        native.External.Add(first);

        Assert.True(sessions.TrySet(first, false));
        Assert.True(native.Query(first));
        Assert.True(native.Query(second));
        Assert.Single(native.Closed);
        Assert.True(sessions.TrySet(second, false));
    }

    [Fact]
    public void RecoveryWithoutAnOwnedClientIsAnIdempotentRelease()
    {
        var native = new Native();
        var sessions = native.Sessions();
        var adapter = Guid.NewGuid();
        native.External.Add(adapter);
        Assert.True(sessions.TrySet(adapter, false));
        Assert.Equal(0, native.Opened);
        Assert.True(native.Query(adapter));
    }

    [Fact]
    public void FailedReadbackReleasesTheNewClient()
    {
        var native = new Native { Readable = false };
        Assert.False(native.Sessions().TrySet(Guid.NewGuid(), true));
        Assert.Single(native.Closed);
        Assert.Empty(native.Requests);
    }

    [Fact]
    public void FailedCloseIsRetriedInsteadOfForgettingTheClient()
    {
        var native = new Native();
        var sessions = native.Sessions();
        var adapter = Guid.NewGuid();
        Assert.True(sessions.TrySet(adapter, true));
        native.CanClose = false;
        Assert.False(sessions.TrySet(adapter, false));
        native.CanClose = true;
        Assert.True(sessions.TrySet(adapter, false));
        Assert.False(native.Query(adapter));
        Assert.Single(native.Closed);
    }

    private sealed class Native
    {
        public int Opened;
        public bool Readable = true;
        public bool CanClose = true;
        public Dictionary<nint, Guid> Requests { get; } = [];
        public HashSet<Guid> External { get; } = [];
        public List<nint> Closed { get; } = [];
        public bool? Query(Guid adapter) => Readable ? External.Contains(adapter) || Requests.ContainsValue(adapter) : null;

        public WlanMediaStreamingSessions Sessions() => new(
            () => ++Opened,
            (client, adapter, enabled) =>
            {
                if (enabled) Requests[client] = adapter;
                else Requests.Remove(client);
                return true;
            },
            Query,
            client =>
            {
                if (!CanClose) return false;
                Requests.Remove(client);
                Closed.Add(client);
                return true;
            });
    }
}
