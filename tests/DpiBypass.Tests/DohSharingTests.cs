using System.Net;
using System.Net.Http.Headers;
using DpiBypass.Core.Dns;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// A scripted DoH endpoint: counts requests, and can be held open on command.
/// </summary>
internal sealed class FakeDohEndpoint : HttpMessageHandler
{
    private readonly Func<byte[], Task<HttpResponseMessage>> _answer;
    private readonly Lock _gate = new();
    private readonly List<string> _urls = [];

    public FakeDohEndpoint(Func<byte[], Task<HttpResponseMessage>> answer) => _answer = answer;

    public int Requests
    {
        get
        {
            lock (_gate)
            {
                return _urls.Count;
            }
        }
    }

    public IReadOnlyList<string> Urls
    {
        get
        {
            lock (_gate)
            {
                return [.. _urls];
            }
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            _urls.Add(request.RequestUri!.ToString());
        }

        var query = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        return await _answer(query).ConfigureAwait(false);
    }

    public static HttpResponseMessage Answer(byte[] payload)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(payload),
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
        return response;
    }

    /// <summary>A well formed answer to whatever was asked.</summary>
    public static byte[] AnswerFor(byte[] query, byte lastOctet = 1)
    {
        Assert.True(DnsMessage.TryReadQuestion(query, out var question));
        var response = DnsUdpSizeTests.BuildResponseWithAnswers(question.Name, 1, DnsMessage.GetId(query));
        response[^1] = lastOctet;
        return response;
    }
}

/// <summary>
/// One upstream request per distinct question, however many clients ask it.
/// </summary>
public sealed class DohSharingTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Fifty simultaneous lookups of one name is one HTTPS request, not fifty.
    /// </summary>
    [Fact]
    public async Task FiftyConcurrentLookupsOfOneNameMakeOneUpstreamRequest()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var endpoint = new FakeDohEndpoint(async query =>
        {
            arrived.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query));
        });

        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: endpoint);

        // Distinct transaction IDs, one question: exactly what a browser opening a page
        // puts through a cold cache.
        var queries = Enumerable.Range(1, 50)
            .Select(i => DnsMessage.BuildQuery((ushort)i, "discord.com", DnsRecordType.A))
            .ToArray();

        var lookups = queries.Select(q => resolver.QueryAsync(q, CancellationToken.None)).ToArray();

        await arrived.Task.WaitAsync(Patience);
        release.SetResult();
        var answers = await Task.WhenAll(lookups).WaitAsync(Patience);

        Assert.Equal(1, endpoint.Requests);
        Assert.Equal(1, resolver.UpstreamQueries);
        Assert.Equal(49, resolver.CoalescedQueries);
        Assert.All(answers, a => Assert.NotNull(a));
    }

    /// <summary>
    /// Every waiter gets its own buffer carrying its own transaction ID.
    /// </summary>
    /// <remarks>
    /// The proxy stamps the client's ID onto the answer before sending it. If waiters
    /// shared one array they would be stamping over each other, and clients would receive
    /// answers bearing somebody else's ID - which a resolver drops as an off-path reply.
    /// </remarks>
    [Fact]
    public async Task EachWaiterGetsItsOwnBufferWithItsOwnTransactionId()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var endpoint = new FakeDohEndpoint(async query =>
        {
            arrived.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query));
        });

        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: endpoint);

        var queries = new[] { (ushort)0x1111, (ushort)0x2222, (ushort)0x3333 }
            .Select(id => DnsMessage.BuildQuery(id, "discord.com", DnsRecordType.A))
            .ToArray();

        var lookups = queries.Select(q => resolver.QueryAsync(q, CancellationToken.None)).ToArray();
        await arrived.Task.WaitAsync(Patience);
        release.SetResult();
        var answers = await Task.WhenAll(lookups).WaitAsync(Patience);

        for (var i = 0; i < queries.Length; i++)
        {
            Assert.NotNull(answers[i]);
            Assert.Equal(DnsMessage.GetId(queries[i]), DnsMessage.GetId(answers[i]!));
            Assert.True(DnsMessage.IsResponseForQuery(queries[i], answers[i]!));
        }

        // Three separate arrays: writing into one must not be visible in another.
        Assert.NotSame(answers[0], answers[1]);
        Assert.NotSame(answers[1], answers[2]);
    }

    /// <summary>
    /// A client giving up must not take the answer away from the others waiting for it.
    /// </summary>
    [Fact]
    public async Task OneClientGivingUpDoesNotCancelTheOthers()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var endpoint = new FakeDohEndpoint(async query =>
        {
            arrived.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query));
        });

        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: endpoint);
        using var impatient = new CancellationTokenSource();

        var query = DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A);
        var leaving = resolver.QueryAsync(query, impatient.Token);

        await arrived.Task.WaitAsync(Patience);

        var staying = Enumerable.Range(2, 4)
            .Select(i => resolver.QueryAsync(
                DnsMessage.BuildQuery((ushort)i, "discord.com", DnsRecordType.A),
                CancellationToken.None))
            .ToArray();

        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => leaving.WaitAsync(Patience));

        release.SetResult();
        var answers = await Task.WhenAll(staying).WaitAsync(Patience);

        Assert.All(answers, a => Assert.NotNull(a));
        Assert.Equal(1, endpoint.Requests);
    }

    /// <summary>
    /// When the last waiter leaves, the shared request is dropped rather than left running.
    /// </summary>
    [Fact]
    public async Task TheSharedRequestIsDroppedOnceTheLastWaiterHasGone()
    {
        var cancelledUpstream = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var endpoint = new FakeDohEndpoint(async query =>
        {
            arrived.TrySetResult();
            try
            {
                await Task.Delay(Timeout.Infinite, CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                cancelledUpstream.TrySetResult();
            }

            return FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query));
        });

        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: endpoint);
        using var caller = new CancellationTokenSource();

        var only = resolver.QueryAsync(DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A), caller.Token);
        await arrived.Task.WaitAsync(Patience);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => only.WaitAsync(Patience));

        // A second lookup of the same name now starts its own request rather than
        // attaching to the abandoned one.
        var second = resolver.QueryAsync(DnsMessage.BuildQuery(2, "discord.com", DnsRecordType.A), CancellationToken.None);
        await arrived.Task.WaitAsync(Patience);
        Assert.Equal(2, endpoint.Requests);

        resolver.Dispose();
        await second.WaitAsync(Patience);
    }

    /// <summary>
    /// Two questions that differ only in the ways that matter are never merged.
    /// </summary>
    /// <remarks>
    /// The key is the whole query with the transaction ID cleared, which keeps the record
    /// type and class, the header flags, and any EDNS or DNSSEC options apart. Merging on
    /// the name alone would answer an AAAA lookup with A records.
    /// </remarks>
    [Fact]
    public async Task DifferentQuestionsAboutOneNameAreNotMerged()
    {
        var endpoint = new FakeDohEndpoint(query =>
            Task.FromResult(FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query))));
        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: endpoint);

        var distinct = new[]
        {
            DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A),
            DnsMessage.BuildQuery(2, "discord.com", DnsRecordType.Aaaa),
            DnsMessage.BuildQuery(3, "discord.com", DnsRecordType.Https),
            DnsMessage.BuildQuery(4, "discord.com", DnsRecordType.A, recursionDesired: false),
            DnsUdpSizeTests.WithEdns(DnsMessage.BuildQuery(5, "discord.com", DnsRecordType.A), 1232),
            DnsUdpSizeTests.WithEdns(DnsMessage.BuildQuery(6, "discord.com", DnsRecordType.A), 4096),
        };

        foreach (var query in distinct)
        {
            await resolver.QueryAsync(query, CancellationToken.None).WaitAsync(Patience);
        }

        Assert.Equal(distinct.Length, endpoint.Requests);
        Assert.Equal(0, resolver.CoalescedQueries);
    }

    /// <summary>
    /// A query the codec cannot read is still sent; it just cannot be shared.
    /// </summary>
    [Fact]
    public async Task AQueryWithNoReadableQuestionIsStillAsked()
    {
        var endpoint = new FakeDohEndpoint(_ =>
            Task.FromResult(FakeDohEndpoint.Answer(new byte[DnsMessage.HeaderLength])));
        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: endpoint);

        var answer = await resolver.QueryAsync(new byte[DnsMessage.HeaderLength], CancellationToken.None)
            .WaitAsync(Patience);

        Assert.Null(answer);
        Assert.Equal(1, endpoint.Requests);
    }
}

/// <summary>
/// Which endpoint is tried first, when a failing one is retried, and what "healthy" means.
/// </summary>
public sealed class DohEndpointHealthTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    /// <summary>
    /// An endpoint that answers a question nobody asked is penalised.
    /// </summary>
    /// <remarks>
    /// This case used to move to the next provider without recording anything, so an
    /// endpoint that reliably answered wrongly kept its place at the head of the chain
    /// and was tried first on every single query for the life of the process.
    /// </remarks>
    [Fact]
    public async Task AnEndpointThatAnswersTheWrongQuestionIsDemoted()
    {
        // Cloudflare answers a question nobody asked; Google answers the real one.
        var liar = new FakeDohEndpoint(query =>
        {
            Assert.True(DnsMessage.TryReadQuestion(query, out var question));
            var wrong = DnsUdpSizeTests.BuildResponseWithAnswers(
                question.Name == "discord.com" ? "example.invalid" : "discord.com",
                1,
                DnsMessage.GetId(query));
            return Task.FromResult(FakeDohEndpoint.Answer(wrong));
        });

        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare, DohResolver.Google],
            transport: new RoutingHandler(
                (DohResolver.Cloudflare.Url, liar),
                (DohResolver.Google.Url, new FakeDohEndpoint(query =>
                    Task.FromResult(FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query)))))));

        var first = await resolver
            .QueryAsync(DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        Assert.NotNull(first);
        Assert.Equal("Google", resolver.ActiveProvider);
        Assert.Equal("Google", resolver.VerifiedProvider);

        var status = resolver.EndpointStatus();
        var cloudflare = status.Single(s => s.Provider == "Cloudflare");
        Assert.False(cloudflare.Healthy);
        Assert.Equal("yanıt sorguyla eşleşmedi", cloudflare.LastFailure);
        Assert.NotNull(cloudflare.PenaltyRemaining);
    }

    /// <summary>
    /// A penalised endpoint is tried last, not dropped, and the failover is reported.
    /// </summary>
    [Fact]
    public async Task AFailingEndpointIsTriedLastAndTheFallbackIsNamedHonestly()
    {
        var dead = new FakeDohEndpoint(_ => throw new HttpRequestException("no route"));
        var alive = new FakeDohEndpoint(query =>
            Task.FromResult(FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query))));

        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare, DohResolver.Quad9],
            transport: new RoutingHandler((DohResolver.Cloudflare.Url, dead), (DohResolver.Quad9.Url, alive)));

        await resolver.QueryAsync(DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        Assert.Equal("Quad9", resolver.ActiveProvider);
        Assert.Equal("Quad9", resolver.VerifiedProvider);

        // Second query goes straight to the one that works.
        await resolver.QueryAsync(DnsMessage.BuildQuery(2, "gateway.discord.gg", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        Assert.Equal(1, dead.Requests);
        Assert.Equal(2, alive.Requests);

        var cloudflare = resolver.EndpointStatus().Single(s => s.Provider == "Cloudflare");
        Assert.False(cloudflare.Healthy);
        Assert.NotNull(cloudflare.PenaltyRemaining);
        Assert.True(cloudflare.PenaltyRemaining > TimeSpan.Zero);
    }

    /// <summary>
    /// Moving to a different network forgets which endpoints were unreachable on the last one.
    /// </summary>
    /// <remarks>
    /// Reachability belongs to the link. A resolver blocked at a hotel is usually the
    /// fastest one at home, and carrying the demotion across the transition means arriving
    /// home stuck on the fallback.
    /// </remarks>
    [Fact]
    public async Task ANetworkChangeClearsTheEndpointPenaltiesAndMovesTheEpoch()
    {
        var flaky = new FlakyHandler();
        var alive = new FakeDohEndpoint(query =>
            Task.FromResult(FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query))));

        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare, DohResolver.Quad9],
            transport: new RoutingHandler((DohResolver.Cloudflare.Url, flaky), (DohResolver.Quad9.Url, alive)));

        await resolver.QueryAsync(DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        Assert.False(resolver.EndpointStatus().Single(s => s.Provider == "Cloudflare").Healthy);

        var before = resolver.Epoch;
        flaky.Working = true;
        resolver.OnNetworkChanged();

        Assert.NotEqual(before, resolver.Epoch);
        Assert.Null(resolver.VerifiedProvider);
        Assert.Equal("ölçülmedi", resolver.EndpointStatus().Single(s => s.Provider == "Cloudflare").LastFailure);

        await resolver.QueryAsync(DnsMessage.BuildQuery(2, "discord.com", DnsRecordType.A), CancellationToken.None)
            .WaitAsync(Patience);

        // The preferred endpoint is at the head of the chain again.
        Assert.Equal("Cloudflare", resolver.VerifiedProvider);
    }

    /// <summary>
    /// An answer that arrives after the machine has moved is returned to whoever asked
    /// for it, and kept out of the new network's cache.
    /// </summary>
    [Fact]
    public async Task AnAnswerFromTheOldNetworkIsNotCachedForTheNewOne()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var endpoint = new FakeDohEndpoint(async query =>
        {
            arrived.TrySetResult();
            await release.Task.ConfigureAwait(false);
            return FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query));
        });

        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: endpoint);
        await using var proxy = new DnsProxyServer(resolver);

        var query = DnsMessage.BuildQuery(0x1234, "discord.com", DnsRecordType.A);
        var inFlight = proxy.ResolveAsync(query, CancellationToken.None);

        await arrived.Task.WaitAsync(Patience);

        // The laptop joins a different network while the lookup is out.
        resolver.OnNetworkChanged();
        proxy.OnNetworkChanged();

        release.SetResult();
        var answer = await inFlight.WaitAsync(Patience);

        // The client that asked still gets its answer.
        Assert.NotNull(answer);
        Assert.True(DnsMessage.IsResponseForQuery(query, answer!));
        Assert.Equal(1, proxy.CrossNetworkDrops);

        // But the new network's cache never saw it: the next lookup goes upstream again.
        var second = DnsMessage.BuildQuery(0x5678, "discord.com", DnsRecordType.A);
        await proxy.ResolveAsync(second, CancellationToken.None).WaitAsync(Patience);

        Assert.Equal(2, endpoint.Requests);
        Assert.Equal(0, proxy.CacheHits);
    }

    /// <summary>A warm answer on the same network is served from cache, as before.</summary>
    [Fact]
    public async Task AWarmAnswerOnTheSameNetworkStillComesFromTheCache()
    {
        var endpoint = new FakeDohEndpoint(query =>
            Task.FromResult(FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query))));
        using var resolver = new DohResolver(chain: [DohResolver.Cloudflare], transport: endpoint);
        await using var proxy = new DnsProxyServer(resolver);

        await proxy.ResolveAsync(DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A), CancellationToken.None);
        var warm = await proxy.ResolveAsync(
            DnsMessage.BuildQuery(2, "discord.com", DnsRecordType.A),
            CancellationToken.None);

        Assert.Equal(1, endpoint.Requests);
        Assert.Equal(1, proxy.CacheHits);
        Assert.Equal(2, DnsMessage.GetId(warm!));
    }

    /// <summary>Sends each endpoint's requests to its own scripted handler.</summary>
    private sealed class RoutingHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpMessageHandler> _routes;
        private readonly Dictionary<HttpMessageHandler, HttpMessageInvoker> _invokers = [];

        public RoutingHandler(params (string Url, HttpMessageHandler Handler)[] routes)
        {
            _routes = routes.ToDictionary(r => r.Url, r => r.Handler, StringComparer.Ordinal);
            foreach (var (_, handler) in routes)
            {
                _invokers[handler] = new HttpMessageInvoker(handler, disposeHandler: false);
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            if (!_routes.TryGetValue(url, out var handler))
            {
                throw new HttpRequestException($"no route for {url}");
            }

            return _invokers[handler].SendAsync(request, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var invoker in _invokers.Values)
                {
                    invoker.Dispose();
                }

                foreach (var handler in _routes.Values)
                {
                    handler.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    /// <summary>Fails until told otherwise.</summary>
    private sealed class FlakyHandler : HttpMessageHandler
    {
        public bool Working { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (!Working)
            {
                throw new HttpRequestException("blocked on this network");
            }

            var query = await request.Content!.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            return FakeDohEndpoint.Answer(FakeDohEndpoint.AnswerFor(query));
        }
    }
}
