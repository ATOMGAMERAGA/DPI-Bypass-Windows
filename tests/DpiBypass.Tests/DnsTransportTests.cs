using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using DpiBypass.Core.Dns;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// Sending a DNS answer over a stream, which is where a dropped return value used to
/// turn a large reply into a message shorter than its own length prefix.
/// </summary>
public sealed class DnsStreamTransportTests
{
    /// <summary>A sink that never takes more than <c>chunk</c> bytes at a time.</summary>
    private sealed class ChunkedSink : IByteSink
    {
        private readonly int _chunk;
        private readonly List<byte> _written = [];

        public ChunkedSink(int chunk) => _chunk = chunk;

        public int Calls { get; private set; }

        public byte[] Written => [.. _written];

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            Calls++;
            var take = Math.Min(_chunk, buffer.Length);
            _written.AddRange(buffer.Span[..take]);
            return ValueTask.FromResult(take);
        }
    }

    private sealed class StallingSink : IByteSink
    {
        private readonly int _acceptFirst;
        private bool _sentOnce;

        public StallingSink(int acceptFirst) => _acceptFirst = acceptFirst;

        public int Calls { get; private set; }

        public ValueTask<int> SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken)
        {
            Calls++;
            if (_sentOnce)
            {
                // The peer went away: a stream socket reports zero progress.
                return ValueTask.FromResult(0);
            }

            _sentOnce = true;
            return ValueTask.FromResult(Math.Min(_acceptFirst, buffer.Length));
        }
    }

    [Fact]
    public async Task AShortWritingSocketStillReceivesEveryByteInOrder()
    {
        var answer = Enumerable.Range(0, 900).Select(i => (byte)(i % 251)).ToArray();
        var framed = DnsStreamTransport.Frame(answer);
        var sink = new ChunkedSink(7);

        Assert.True(await DnsStreamTransport.SendAllAsync(sink, framed, CancellationToken.None));

        Assert.Equal(framed, sink.Written);
        Assert.True(sink.Calls > 1, "the test is meaningless unless the sink actually short-wrote");
        Assert.Equal(900, BinaryPrimitives.ReadUInt16BigEndian(sink.Written));
    }

    [Fact]
    public async Task ASocketThatStopsMakingProgressEndsTheSendRatherThanSpinning()
    {
        var sink = new StallingSink(acceptFirst: 4);

        Assert.False(await DnsStreamTransport.SendAllAsync(sink, new byte[512], CancellationToken.None));

        // One partial write, one zero, and out. A retry loop would run forever here.
        Assert.Equal(2, sink.Calls);
    }

    [Fact]
    public async Task CancellationEndsTheSendLoop()
    {
        using var cancellation = new CancellationTokenSource();
        var sink = new ChunkedSink(1);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await DnsStreamTransport.SendAllAsync(sink, new byte[16], cancellation.Token));
    }

    [Fact]
    public async Task AnEmptyPayloadIsAlreadySent()
    {
        var sink = new ChunkedSink(1);

        Assert.True(await DnsStreamTransport.SendAllAsync(sink, ReadOnlyMemory<byte>.Empty, CancellationToken.None));
        Assert.Equal(0, sink.Calls);
    }

    [Fact]
    public void TheFrameCarriesTheMessageLengthInTwoBigEndianBytes()
    {
        var framed = DnsStreamTransport.Frame(new byte[300]);

        Assert.Equal(302, framed.Length);
        Assert.Equal(300, BinaryPrimitives.ReadUInt16BigEndian(framed));
    }
}

/// <summary>
/// What a client is told when an answer does not fit in the datagram it can receive.
/// </summary>
public sealed class DnsUdpSizeTests
{
    [Fact]
    public void AClientWithoutEdnsGetsTheClassicFiveHundredAndTwelve()
    {
        var query = DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A);

        Assert.Equal(DnsMessage.ClassicUdpPayloadSize, DnsMessage.GetClientUdpPayloadSize(query));
    }

    [Theory]
    [InlineData(1232, 1232)]
    [InlineData(4096, 4096)]
    [InlineData(8192, 4096)] // more than anything on the path carries unfragmented
    [InlineData(120, 512)]   // less than the classic minimum no resolver honours
    public void AnEdnsClientIsTakenAtItsWordWithinSaneBounds(int advertised, int expected)
    {
        var query = WithEdns(DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A), advertised);

        Assert.Equal(expected, DnsMessage.GetClientUdpPayloadSize(query));
    }

    [Fact]
    public void ATruncatedAnswerKeepsTheQuestionAndPromisesNoRecords()
    {
        var response = BuildResponseWithAnswers("discord.com", answers: 3);

        var truncated = DnsMessage.BuildTruncatedResponse(response);

        Assert.True(DnsMessage.IsTruncated(truncated));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(truncated.AsSpan(6)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(truncated.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(truncated.AsSpan(10)));
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(truncated.AsSpan(4)));
        Assert.True(truncated.Length < response.Length);

        // Still a well formed answer to the question that was asked, which is what makes
        // the client retry over TCP rather than treat it as garbage.
        Assert.True(DnsMessage.TryReadQuestion(truncated, out var question));
        Assert.Equal("discord.com", question.Name);
        Assert.Equal(DnsRecordType.A, question.Type);
    }

    [Fact]
    public void ATruncatedAnswerStillMatchesTheQueryItAnswers()
    {
        var query = DnsMessage.BuildQuery(0x4321, "discord.com", DnsRecordType.A);
        var response = BuildResponseWithAnswers("discord.com", answers: 5, id: 0x4321);

        var truncated = DnsMessage.BuildTruncatedResponse(response);

        Assert.True(DnsMessage.IsResponseForQuery(query, truncated));
    }

    internal static byte[] WithEdns(byte[] query, int payloadSize)
    {
        // One OPT record in the additional section: empty name, type 41, CLASS = size.
        var opt = new byte[11];
        opt[0] = 0; // root name
        BinaryPrimitives.WriteUInt16BigEndian(opt.AsSpan(1), 41);
        BinaryPrimitives.WriteUInt16BigEndian(opt.AsSpan(3), (ushort)payloadSize);
        BinaryPrimitives.WriteUInt32BigEndian(opt.AsSpan(5), 0);
        BinaryPrimitives.WriteUInt16BigEndian(opt.AsSpan(9), 0);

        var extended = new byte[query.Length + opt.Length];
        query.CopyTo(extended, 0);
        opt.CopyTo(extended, query.Length);
        BinaryPrimitives.WriteUInt16BigEndian(extended.AsSpan(10), 1); // ARCOUNT
        return extended;
    }

    internal static byte[] BuildResponseWithAnswers(string name, int answers, ushort id = 1)
    {
        var query = DnsMessage.BuildQuery(id, name, DnsRecordType.A);
        var record = new List<byte>();

        for (var i = 0; i < answers; i++)
        {
            record.AddRange([0xC0, 0x0C]); // pointer to the question's name
            record.AddRange([0x00, 0x01]); // A
            record.AddRange([0x00, 0x01]); // IN
            record.AddRange([0x00, 0x00, 0x01, 0x2C]); // TTL 300
            record.AddRange([0x00, 0x04]);
            record.AddRange([(byte)(10 + i), 0, 0, 1]);
        }

        var response = new byte[query.Length + record.Count];
        query.CopyTo(response, 0);
        record.CopyTo(response, query.Length);
        response[2] |= 0x80; // QR
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), (ushort)answers);
        return response;
    }
}

/// <summary>
/// The proxy driven over real loopback sockets, because framing, truncation and
/// connection reuse are behaviours of the wire, not of a method call.
/// </summary>
public sealed class DnsProxyWireTests
{
    /// <summary>Answers every DoH request with a fixed number of A records.</summary>
    private sealed class ScriptedDoh : HttpMessageHandler
    {
        private readonly int _answers;

        public ScriptedDoh(int answers) => _answers = answers;

        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            var query = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Assert.True(DnsMessage.TryReadQuestion(query, out var question));

            var payload = DnsUdpSizeTests.BuildResponseWithAnswers(
                question.Name,
                _answers,
                DnsMessage.GetId(query));

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            return response;
        }
    }

    private static int FreePort()
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        probe.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.LocalEndPoint!).Port;
    }

    /// <summary>
    /// Two questions down one TCP connection, both answered in full.
    /// </summary>
    /// <remarks>
    /// The second query is what RFC 7766 §6.2.1 asks for and what a resolver retrying a
    /// truncated answer does; the first is the one whose reply used to be able to arrive
    /// half sent. Sixteen A records is comfortably past a single small send buffer.
    /// </remarks>
    [Fact]
    public async Task SuccessiveQueriesOnOneTcpConnectionAreEachAnsweredInFull()
    {
        var port = FreePort();
        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare],
            transport: new ScriptedDoh(answers: 16));
        await using var proxy = new DnsProxyServer(resolver);

        Assert.True(proxy.TryStart(port));

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);

        foreach (var name in new[] { "discord.com", "gateway.discord.gg" })
        {
            var query = DnsMessage.BuildQuery(0x1234, name, DnsRecordType.A);
            await client.SendAsync(DnsStreamTransport.Frame(query), SocketFlags.None);

            var answer = await ReadFramedAsync(client);

            Assert.True(DnsMessage.IsResponseForQuery(query, answer));
            Assert.Equal(16, DnsMessage.ReadAnswers(answer).Count);
        }

        Assert.Equal(0, proxy.AbandonedTcpAnswers);
    }

    /// <summary>
    /// A UDP answer larger than the client's buffer comes back truncated, not chopped.
    /// </summary>
    [Fact]
    public async Task ALargeUdpAnswerComesBackTruncatedRatherThanCutInHalf()
    {
        var port = FreePort();
        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare],
            transport: new ScriptedDoh(answers: 40));
        await using var proxy = new DnsProxyServer(resolver);

        Assert.True(proxy.TryStart(port));

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var query = DnsMessage.BuildQuery(0x2222, "discord.com", DnsRecordType.A);
        await client.SendToAsync(query, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));

        var buffer = new byte[4096];
        var received = await client
            .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0))
            .WaitAsync(TimeSpan.FromSeconds(10));

        var answer = buffer[..received.ReceivedBytes];

        Assert.True(DnsMessage.IsTruncated(answer));
        Assert.True(DnsMessage.IsResponseForQuery(query, answer));
        Assert.Empty(DnsMessage.ReadAnswers(answer));
        Assert.True(answer.Length <= DnsMessage.ClassicUdpPayloadSize);
        Assert.Equal(1, proxy.TruncatedAnswers);
    }

    /// <summary>The same answer fits, and is sent whole, once the client says it can take it.</summary>
    [Fact]
    public async Task AnEdnsClientReceivesTheWholeAnswerInOneDatagram()
    {
        var port = FreePort();
        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare],
            transport: new ScriptedDoh(answers: 40));
        await using var proxy = new DnsProxyServer(resolver);

        Assert.True(proxy.TryStart(port));

        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        var query = DnsUdpSizeTests.WithEdns(DnsMessage.BuildQuery(0x3333, "discord.com", DnsRecordType.A), 4096);
        await client.SendToAsync(query, SocketFlags.None, new IPEndPoint(IPAddress.Loopback, port));

        var buffer = new byte[4096];
        var received = await client
            .ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0))
            .WaitAsync(TimeSpan.FromSeconds(10));

        var answer = buffer[..received.ReceivedBytes];

        Assert.False(DnsMessage.IsTruncated(answer));
        Assert.Equal(40, DnsMessage.ReadAnswers(answer).Count);
        Assert.Equal(0, proxy.TruncatedAnswers);
    }

    /// <summary>
    /// A client that connects, asks, and vanishes mid-answer leaves nothing hung behind it.
    /// </summary>
    [Fact]
    public async Task AClientThatDisappearsDoesNotLeaveTheProxyHoldingTheConnection()
    {
        var port = FreePort();
        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare],
            transport: new ScriptedDoh(answers: 8));
        await using var proxy = new DnsProxyServer(resolver);

        Assert.True(proxy.TryStart(port));

        for (var i = 0; i < 16; i++)
        {
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, port);
            await client.SendAsync(
                DnsStreamTransport.Frame(DnsMessage.BuildQuery((ushort)(i + 1), "discord.com", DnsRecordType.A)),
                SocketFlags.None);

            // Gone before the answer is read, over and over.
            client.Close();
        }

        // The proxy is still serving: the request slots came back.
        using var survivor = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await survivor.ConnectAsync(IPAddress.Loopback, port);
        var query = DnsMessage.BuildQuery(0x7777, "discord.com", DnsRecordType.A);
        await survivor.SendAsync(DnsStreamTransport.Frame(query), SocketFlags.None);

        var answer = await ReadFramedAsync(survivor);
        Assert.True(DnsMessage.IsResponseForQuery(query, answer));
    }

    /// <summary>A half sent length prefix must not leave the handler waiting for ever.</summary>
    [Fact]
    public async Task AConnectionThatSendsNothingIsClosedRatherThanHeld()
    {
        var port = FreePort();
        using var resolver = new DohResolver(
            chain: [DohResolver.Cloudflare],
            transport: new ScriptedDoh(answers: 2));
        await using var proxy = new DnsProxyServer(resolver);

        Assert.True(proxy.TryStart(port));

        using var silent = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await silent.ConnectAsync(IPAddress.Loopback, port);
        await silent.SendAsync(new byte[] { 0x00 }, SocketFlags.None); // half a length prefix

        // Everyone else is still served while that one sits there.
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await client.ConnectAsync(IPAddress.Loopback, port);
        var query = DnsMessage.BuildQuery(0x8888, "discord.com", DnsRecordType.A);
        await client.SendAsync(DnsStreamTransport.Frame(query), SocketFlags.None);

        var answer = await ReadFramedAsync(client);
        Assert.True(DnsMessage.IsResponseForQuery(query, answer));
    }

    private static async Task<byte[]> ReadFramedAsync(Socket socket)
    {
        var prefix = new byte[2];
        await ReadExactAsync(socket, prefix);
        var body = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)];
        await ReadExactAsync(socket, body);
        return body;
    }

    private static async Task ReadExactAsync(Socket socket, byte[] buffer)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await socket
                .ReceiveAsync(buffer.AsMemory(offset), SocketFlags.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(read > 0, "the peer closed before sending the whole message");
            offset += read;
        }
    }
}
