using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Dns;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Interop;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// When the watchdog is allowed to rebuild something, and how quickly it gives up on
/// hammering it.
/// </summary>
public sealed class RecoveryScheduleTests
{
    private static readonly DateTimeOffset Origin = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void TheFirstRebuildIsTriedStraightAway()
    {
        var schedule = new RecoverySchedule();

        Assert.True(schedule.ShouldAttempt(Origin));
        Assert.Equal(0, schedule.Attempts);
        Assert.Null(schedule.NextAttempt);
    }

    /// <summary>
    /// Each failure waits twice as long as the last, so a driver that cannot be opened at
    /// all is not reopened several times a second for the rest of the session.
    /// </summary>
    [Fact]
    public void EachFailureWaitsTwiceAsLongAsTheLast()
    {
        var schedule = new RecoverySchedule(firstDelay: TimeSpan.FromSeconds(15), ceiling: TimeSpan.FromMinutes(5));

        Assert.Equal(TimeSpan.FromSeconds(15), schedule.RecordAttempt(Origin, succeeded: false));
        Assert.Equal(TimeSpan.FromSeconds(30), schedule.RecordAttempt(Origin, succeeded: false));
        Assert.Equal(TimeSpan.FromSeconds(60), schedule.RecordAttempt(Origin, succeeded: false));
        Assert.Equal(TimeSpan.FromSeconds(120), schedule.RecordAttempt(Origin, succeeded: false));
        Assert.Equal(TimeSpan.FromSeconds(240), schedule.RecordAttempt(Origin, succeeded: false));

        // And never past the ceiling, however long the session runs.
        Assert.Equal(TimeSpan.FromMinutes(5), schedule.RecordAttempt(Origin, succeeded: false));
        Assert.Equal(TimeSpan.FromMinutes(5), schedule.RecordAttempt(Origin, succeeded: false));
    }

    [Fact]
    public void NothingIsRetriedBeforeItsTurn()
    {
        var schedule = new RecoverySchedule(firstDelay: TimeSpan.FromSeconds(15));

        schedule.RecordAttempt(Origin, succeeded: false);

        Assert.False(schedule.ShouldAttempt(Origin));
        Assert.False(schedule.ShouldAttempt(Origin.AddSeconds(14)));
        Assert.True(schedule.ShouldAttempt(Origin.AddSeconds(15)));
    }

    /// <summary>
    /// A rebuild that worked is still counted, because something needing one every minute
    /// is not healthy - and starting the backoff from zero on each brief success is how a
    /// watchdog turns a flapping driver into a busy loop.
    /// </summary>
    [Fact]
    public void ARebuildThatWorkedStillCountsAgainstTheNextOne()
    {
        var schedule = new RecoverySchedule(firstDelay: TimeSpan.FromSeconds(15));

        schedule.RecordAttempt(Origin, succeeded: true);

        Assert.Equal(1, schedule.Attempts);
        Assert.False(schedule.ShouldAttempt(Origin.AddSeconds(5)));
        Assert.True(schedule.ShouldAttempt(Origin.AddSeconds(15)));
    }

    /// <summary>
    /// Once it has stayed up, its history stops describing it and is dropped.
    /// </summary>
    [Fact]
    public void StayingUpLongEnoughForgetsTheBackoff()
    {
        var schedule = new RecoverySchedule(
            firstDelay: TimeSpan.FromSeconds(15),
            settled: TimeSpan.FromMinutes(5));

        schedule.RecordAttempt(Origin, succeeded: false);
        schedule.RecordAttempt(Origin.AddSeconds(15), succeeded: true);
        Assert.Equal(2, schedule.Attempts);

        // Healthy, but not for long enough yet.
        Assert.False(schedule.NoteHealthy(Origin.AddSeconds(20)));
        Assert.False(schedule.NoteHealthy(Origin.AddMinutes(4)));
        Assert.Equal(2, schedule.Attempts);

        Assert.True(schedule.NoteHealthy(Origin.AddMinutes(6)));
        Assert.Equal(0, schedule.Attempts);
        Assert.True(schedule.ShouldAttempt(Origin.AddMinutes(6)));
    }

    /// <summary>The healthy stretch is measured from the first healthy tick, not the last.</summary>
    [Fact]
    public void AFlapRestartsTheHealthyStretch()
    {
        var schedule = new RecoverySchedule(
            firstDelay: TimeSpan.FromSeconds(15),
            settled: TimeSpan.FromMinutes(5));

        schedule.RecordAttempt(Origin, succeeded: true);
        Assert.False(schedule.NoteHealthy(Origin.AddMinutes(4)));

        // Down again, and rebuilt again: the four healthy minutes do not carry over.
        schedule.RecordAttempt(Origin.AddMinutes(4), succeeded: true);
        Assert.False(schedule.NoteHealthy(Origin.AddMinutes(8)));
        Assert.True(schedule.NoteHealthy(Origin.AddMinutes(10)));
    }
}

/// <summary>
/// Which driver refusals describe one packet and which describe a dead handle.
/// </summary>
/// <remarks>
/// The engine used to release its filter on the first refused re-injection, so a Wi-Fi
/// roam - a route that has gone for a few milliseconds - could take protection off the
/// machine for the rest of the session.
/// </remarks>
public sealed class TransientSendErrorTests
{
    [Theory]
    [InlineData(1231)] // ERROR_NETWORK_UNREACHABLE
    [InlineData(1232)] // ERROR_HOST_UNREACHABLE
    [InlineData(1450)] // ERROR_NO_SYSTEM_RESOURCES
    [InlineData(1453)] // ERROR_WORKING_SET_QUOTA
    [InlineData(8)]    // ERROR_NOT_ENOUGH_MEMORY
    [InlineData(21)]   // ERROR_NOT_READY
    [InlineData(87)]   // ERROR_INVALID_PARAMETER - this packet, not this handle
    [InlineData(299)]  // a short write
    public void ARouteThatMovedIsOnePacketsProblem(int error)
        => Assert.True(WinDivertHandle.IsTransientSendError(error));

    [Theory]
    [InlineData(6)]    // ERROR_INVALID_HANDLE
    [InlineData(232)]  // ERROR_NO_DATA
    [InlineData(995)]  // ERROR_OPERATION_ABORTED
    [InlineData(1168)] // ERROR_NOT_FOUND
    [InlineData(5)]    // ERROR_ACCESS_DENIED
    public void AHandleThatIsFinishedIsNotWorthRetrying(int error)
        => Assert.False(WinDivertHandle.IsTransientSendError(error));
}

/// <summary>
/// The engine's lifecycle without a driver underneath it.
/// </summary>
public sealed class EngineLifecycleTests
{
    [Fact]
    public void StoppingAnEngineThatNeverStartedDoesNothing()
    {
        using var engine = new BypassEngine(new TargetMatcher());

        engine.Stop();
        engine.Stop();

        Assert.False(engine.IsRunning);
    }

    /// <summary>
    /// A watchdog restart that arrives after the shutdown must not reopen the driver:
    /// nothing would be left to close the rule it installs.
    /// </summary>
    [Fact]
    public void ARestartThatLosesToTheShutdownIsRefused()
    {
        var engine = new BypassEngine(new TargetMatcher());
        engine.Dispose();

        Assert.False(engine.Restart());
        Assert.False(engine.IsRunning);
    }
}

/// <summary>
/// The discovery pass measures one socket, not one hostname.
/// </summary>
/// <remarks>
/// The control arm has to reach the site with no bypass applied, and the site being
/// measured is one the machine resolved seconds ago - so the user is very likely opening
/// it in a browser at that moment. Exempting the whole hostname meant their handshake
/// went out untouched too and was reset by the inspector: a page that failed for no
/// reason they could see, in the middle of a background measurement.
/// </remarks>
public sealed class ProbeScopeTests
{
    private static TargetMatcher Everything() => new() { Scope = ProtectionScope.Everything };

    [Fact]
    public void OnlyTheProbesOwnConnectionSkipsTheBypass()
    {
        var matcher = Everything();
        matcher.ProbePassthroughPort = 54321;
        matcher.ProbePassthroughHost = "example.com";

        Assert.False(matcher.ShouldProtect("example.com", imagePath: null, sourcePort: 54321));

        // The browser sitting on the same site keeps its protection.
        Assert.True(matcher.ShouldProtect("example.com", imagePath: null, sourcePort: 54322));
        Assert.True(matcher.ShouldProtect("example.com", imagePath: null, sourcePort: 1));
    }

    /// <summary>Before the probe's socket exists there is no port, and no exemption either.</summary>
    [Fact]
    public void AnUnknownPortExemptsTheWholeHostAsBefore()
    {
        var matcher = Everything();
        matcher.ProbePassthroughHost = "example.com";
        matcher.ProbePassthroughPort = 0;

        Assert.False(matcher.ShouldProtect("example.com", imagePath: null, sourcePort: 4444));
    }

    [Fact]
    public void ClearingTheProbeRestoresEveryConnection()
    {
        var matcher = Everything();
        matcher.ProbePassthroughPort = 54321;
        matcher.ProbePassthroughHost = "example.com";
        matcher.ProbePassthroughHost = null;
        matcher.ProbePassthroughPort = 0;

        Assert.True(matcher.ShouldProtect("example.com", imagePath: null, sourcePort: 54321));
    }

    [Fact]
    public void TheForcedHostIsUnaffectedByTheScoping()
    {
        var matcher = new TargetMatcher { Scope = ProtectionScope.DiscordOnly };
        matcher.ProbeForcedHost = "example.com";

        Assert.True(matcher.ShouldProtect("example.com", imagePath: null, sourcePort: 999));
    }

    /// <summary>The old two-argument call still means "no port known".</summary>
    [Fact]
    public void TheCallWithoutAPortStillWorks()
    {
        var matcher = Everything();
        matcher.ProbePassthroughHost = "example.com";

        Assert.False(matcher.ShouldProtect("example.com", imagePath: null));
    }
}

/// <summary>
/// A resolver that will not answer must not hold up every lookup behind it.
/// </summary>
public sealed class DohHedgingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An endpoint that accepts the request and then says nothing used to cost every
    /// single lookup its whole per-endpoint budget before the fallback was even tried.
    /// </summary>
    [Fact]
    public async Task AnEndpointThatStallsDoesNotHoldTheQueryUp()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var silent = new FakeDohEndpoint(async _ =>
        {
            await stalled.Task.ConfigureAwait(false);
            throw new HttpRequestException("never answers");
        });

        var quick = new FakeDohEndpoint(query =>
            Task.FromResult(FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query))));

        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare, DohResolver.Quad9],
            perEndpointTimeout: TimeSpan.FromSeconds(30),
            overallTimeout: TimeSpan.FromSeconds(60),
            transport: new ByUrlHandler(
                (DohResolver.Cloudflare.Url, silent),
                (DohResolver.Quad9.Url, quick)));

        var answer = await resolver
            .QueryAsync(DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        // Answered by the second endpoint while the first was still holding its socket
        // open - without hedging this would have taken the full thirty seconds.
        Assert.NotNull(answer);
        Assert.Equal("Quad9", resolver.ActiveProvider);
        Assert.Equal(1, silent.Requests);
        Assert.Equal(1, quick.Requests);

        stalled.SetResult();
    }

    /// <summary>
    /// Losing a race is not a fault. Recording it as one would demote a healthy resolver
    /// for a minute every time a faster one happened to answer first.
    /// </summary>
    [Fact]
    public async Task AnEndpointThatMerelyLostTheRaceIsNotPenalised()
    {
        var stalled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var slow = new FakeDohEndpoint(async query =>
        {
            await stalled.Task.ConfigureAwait(false);
            return FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query));
        });

        var quick = new FakeDohEndpoint(query =>
            Task.FromResult(FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query))));

        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare, DohResolver.Quad9],
            perEndpointTimeout: TimeSpan.FromSeconds(30),
            overallTimeout: TimeSpan.FromSeconds(60),
            transport: new ByUrlHandler(
                (DohResolver.Cloudflare.Url, slow),
                (DohResolver.Quad9.Url, quick)));

        await resolver
            .QueryAsync(DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        var cloudflare = resolver.EndpointStatus().Single(s => s.Provider == "Cloudflare");
        Assert.Equal("ölçülmedi", cloudflare.LastFailure);
        Assert.Null(cloudflare.PenaltyRemaining);

        stalled.SetResult();
    }

    /// <summary>
    /// On a working link the head start never runs out, so nothing extra is ever sent.
    /// </summary>
    [Fact]
    public async Task AResolverThatAnswersPromptlyIsTheOnlyOneAsked()
    {
        var quick = new FakeDohEndpoint(query =>
            Task.FromResult(FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query))));
        var second = new FakeDohEndpoint(query =>
            Task.FromResult(FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query))));

        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare, DohResolver.Quad9],
            transport: new ByUrlHandler(
                (DohResolver.Cloudflare.Url, quick),
                (DohResolver.Quad9.Url, second)));

        for (var i = 1; i <= 5; i++)
        {
            await resolver
                .QueryAsync(DnsMessage.BuildQuery((ushort)i, $"site{i}.example", DnsRecordType.A), CancellationToken.None)
                .WaitAsync(Patience);
        }

        Assert.Equal(5, quick.Requests);
        Assert.Equal(0, second.Requests);
    }

    /// <summary>Sends each endpoint's requests to its own scripted handler.</summary>
    private sealed class ByUrlHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpMessageInvoker> _routes;

        public ByUrlHandler(params (string Url, HttpMessageHandler Handler)[] routes)
            => _routes = routes.ToDictionary(
                route => route.Url,
                route => new HttpMessageInvoker(route.Handler, disposeHandler: true),
                StringComparer.Ordinal);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            return _routes.TryGetValue(url, out var invoker)
                ? invoker.SendAsync(request, cancellationToken)
                : throw new HttpRequestException($"no route for {url}");
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var invoker in _routes.Values)
                {
                    invoker.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }
}

/// <summary>
/// What the loopback DNS server does when the resolvers stop answering, and how it
/// reports whether it is still serving at all.
/// </summary>
public sealed class DnsProxyResilienceTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// A machine whose resolvers point at 127.0.0.1 has no name resolution at all if this
    /// listener stops serving, and "started and not yet disposed" cannot detect that.
    /// </summary>
    [Fact]
    public async Task AProxyReportsWhetherItIsStillServing()
    {
        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare],
            transport: new StaticDoh(query => FakeDohEndpoint.AnswerFor(query)));
        var proxy = new DnsProxyServer(resolver);

        Assert.False(proxy.IsHealthy);

        Assert.True(proxy.TryStart(FreePort()));
        Assert.True(proxy.IsHealthy);

        await proxy.DisposeAsync();
        Assert.False(proxy.IsHealthy);
    }

    /// <summary>
    /// While nothing upstream is answering, an address that is slightly out of date beats
    /// a failure - the failure is what the user experiences as the internet stopping.
    /// </summary>
    [Fact]
    public async Task AnExpiredAnswerIsStillServedWhileTheResolversAreUnreachable()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var upstream = new SwitchableDoh(ExpiringAnswer);
        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: upstream);
        await using var proxy = new DnsProxyServer(resolver) { Clock = clock };

        var first = await proxy
            .ResolveAsync(DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        Assert.NotNull(first);
        Assert.Equal(0, DnsMessage.GetResponseCode(first!));
        Assert.Equal(0, proxy.StaleAnswers);

        // Ten minutes on, the entry is well past its five minute TTL and the link has
        // gone. Before the stale window was widened this was a SERVFAIL, which is the
        // name simply ceasing to resolve for as long as the outage lasts.
        clock.Advance(TimeSpan.FromMinutes(10));
        upstream.Working = false;

        var second = await proxy
            .ResolveAsync(DnsMessage.BuildQuery(2, "discord.com", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        Assert.NotNull(second);
        Assert.Equal(0, DnsMessage.GetResponseCode(second!));
        Assert.Equal(2, DnsMessage.GetId(second!));
        Assert.Single(DnsMessage.ReadAddresses(second!));
        Assert.Equal(1, proxy.StaleAnswers);
    }

    /// <summary>
    /// The window ends. An address a day old is not an answer, it is a guess, and past
    /// the window the honest failure is the better of the two.
    /// </summary>
    [Fact]
    public async Task AnAnswerTooOldToTrustIsNotServed()
    {
        var clock = new FakeClock(new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var upstream = new SwitchableDoh(ExpiringAnswer);
        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: upstream);
        await using var proxy = new DnsProxyServer(resolver) { Clock = clock };

        await proxy
            .ResolveAsync(DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        clock.Advance(TimeSpan.FromHours(6));
        upstream.Working = false;

        var answer = await proxy
            .ResolveAsync(DnsMessage.BuildQuery(2, "discord.com", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        Assert.NotNull(answer);
        Assert.Equal(2, DnsMessage.GetResponseCode(answer!)); // SERVFAIL
        Assert.Equal(0, proxy.StaleAnswers);
    }

    /// <summary>A clock the test moves by hand.</summary>
    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan delta) => _now += delta;
    }

    /// <summary>A name nobody has ever resolved here still fails honestly.</summary>
    [Fact]
    public async Task ANameWithNothingCachedStillFailsWhenNothingAnswers()
    {
        var upstream = new SwitchableDoh(ExpiringAnswer) { Working = false };
        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: upstream);
        await using var proxy = new DnsProxyServer(resolver);

        var answer = await proxy
            .ResolveAsync(DnsMessage.BuildQuery(7, "never-seen.example", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        Assert.NotNull(answer);
        Assert.Equal(2, DnsMessage.GetResponseCode(answer!)); // SERVFAIL
        Assert.Equal(0, proxy.StaleAnswers);
    }

    /// <summary>One A record with a zero TTL, so it is stale the moment it is cached.</summary>
    private static byte[] ExpiringAnswer(byte[] query)
    {
        Assert.True(DnsMessage.TryReadQuestion(query, out _));

        var record = new List<byte>();
        record.AddRange([0xC0, 0x0C]); // pointer to the question's name
        record.AddRange([0x00, 0x01]); // A
        record.AddRange([0x00, 0x01]); // IN
        record.AddRange([0x00, 0x00, 0x00, 0x00]); // TTL 0
        record.AddRange([0x00, 0x04]);
        record.AddRange([(byte)10, 0, 0, 1]);

        var response = new byte[query.Length + record.Count];
        query.CopyTo(response, 0);
        record.CopyTo(response, query.Length);
        response[2] |= 0x80; // QR
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), 1);
        return response;
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    private sealed class StaticDoh(Func<byte[], byte[]> answer) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return Respond(answer(query));
        }

        internal static HttpResponseMessage Respond(byte[] payload)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            return response;
        }
    }

    /// <summary>Answers until it is told the link has gone.</summary>
    private sealed class SwitchableDoh(Func<byte[], byte[]> answer) : HttpMessageHandler
    {
        public bool Working { get; set; } = true;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var query = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);

            if (!Working)
            {
                throw new HttpRequestException("the link has gone");
            }

            return StaticDoh.Respond(answer(query));
        }
    }
}
