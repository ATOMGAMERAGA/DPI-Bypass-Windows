using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using DpiBypass.Core;
using DpiBypass.Core.Apps;
using DpiBypass.Core.Config;
using DpiBypass.Core.Dns;
using Xunit;

namespace DpiBypass.Tests;

/// <summary>
/// The Lunar Client advertisement block, and the two promises it makes.
/// </summary>
/// <remarks>
/// The promises are what these tests are for. The first is that it blocks the
/// advertisement and telemetry hosts and nothing else - a list that quietly grew a name
/// the launcher needs would break signing in, which is worse than the advertisement it
/// was meant to remove. The second is that it costs nothing: a blocked name is answered
/// from a set without reaching the resolver, and an unblocked one is not touched at all.
/// </remarks>
public sealed class LunarAdBlockTests
{
    [Theory]
    [InlineData("ads.overwolf.com")]
    [InlineData("tracking.overwolf.com")]
    [InlineData("analyticsnew.overwolf.com")]
    [InlineData("analytics.lunarclientprod.com")]
    [InlineData("mrkt.forgecdn.net")]
    [InlineData("rumcdn.geoedge.be")]
    [InlineData("pagead2.googlesyndication.com")]
    [InlineData("securepubads.g.doubleclick.net")]
    [InlineData("ads.pubmatic.com")]
    [InlineData("overwolf-d.openx.net")]
    [InlineData("ep2.adtrafficquality.google")]
    [InlineData("ADS.OVERWOLF.COM")]
    [InlineData("ads.overwolf.com.")]
    public void AdvertisingHostsAndEverythingUnderThemAreBlocked(string name)
        => Assert.True(LunarAdBlock.Blocks(name), name);

    /// <summary>
    /// The names the launcher and the game actually need are not on the list.
    /// </summary>
    /// <remarks>
    /// Signing in, the cosmetics, the launcher's own updates and every Minecraft server
    /// the user connects to. If any of these ever starts matching, the feature has stopped
    /// being an advertisement block and become an outage.
    /// </remarks>
    [Theory]
    [InlineData("api.lunarclientprod.com")]
    [InlineData("assetserver.lunarclientprod.com")]
    [InlineData("www.lunarclient.com")]
    [InlineData("lunarclient.com")]
    [InlineData("launcherupdates.lunarclientcdn.com")]
    [InlineData("hypixel.net")]
    [InlineData("mc.hypixel.net")]
    [InlineData("sessionserver.mojang.com")]
    [InlineData("minecraft.net")]
    [InlineData("discord.com")]
    [InlineData("edge.forgecdn.net")]
    [InlineData("media.forgecdn.net")]
    [InlineData("overwolf.com")]
    [InlineData("www.overwolf.com")]
    [InlineData("content.overwolf.com")]
    public void TheNamesTheLauncherNeedsAreNotBlocked(string name)
        => Assert.False(LunarAdBlock.Blocks(name), name);

    /// <summary>
    /// Matching is by label, so a name that merely ends with a blocked string is safe.
    /// </summary>
    /// <remarks>
    /// The difference between a suffix match and a domain match, and the reason the
    /// walk splits on dots rather than calling EndsWith: <c>notoverwolf.com</c> and
    /// <c>evil-doubleclick.net</c> are somebody else's domains entirely.
    /// </remarks>
    [Theory]
    [InlineData("notoverwolf.com")]
    [InlineData("myopenx.net")]
    [InlineData("evil-doubleclick.net")]
    [InlineData("xgeoedge.be")]
    [InlineData("")]
    [InlineData(null)]
    public void ASuffixThatIsNotALabelBoundaryIsNotAMatch(string? name)
        => Assert.False(LunarAdBlock.Blocks(name));

    /// <summary>Every name the hosts layer writes is one the resolver layer also refuses.</summary>
    /// <remarks>
    /// The two lists are written out separately - a hosts entry matches one name exactly,
    /// so it cannot carry the registrable domains - and this is what stops them drifting
    /// into disagreeing about what the feature blocks.
    /// </remarks>
    [Fact]
    public void TheHostsFileNamesAreASubsetOfWhatTheResolverBlocks()
    {
        Assert.NotEmpty(LunarAdBlock.HostsFileNames);
        Assert.All(LunarAdBlock.HostsFileNames, name => Assert.True(LunarAdBlock.Blocks(name), name));
    }

    /// <summary>No entry is covered by another, and none of them is a bare public suffix.</summary>
    [Fact]
    public void TheListIsFreeOfRedundantAndDangerouslyBroadEntries()
    {
        Assert.All(LunarAdBlock.Domains, domain =>
        {
            Assert.Equal(domain.Trim().ToLowerInvariant(), domain);
            Assert.Contains('.', domain);

            // A single label ("com") would take the whole top level down with it.
            Assert.True(domain.Split('.').Length >= 2, domain);
        });

        foreach (var domain in LunarAdBlock.Domains)
        {
            var covered = LunarAdBlock.Domains
                .Where(other => !string.Equals(other, domain, StringComparison.OrdinalIgnoreCase))
                .Where(other => domain.EndsWith($".{other}", StringComparison.OrdinalIgnoreCase))
                .ToArray();

            Assert.True(covered.Length == 0, $"{domain} is already covered by {string.Join(", ", covered)}");
        }

        Assert.Equal(
            LunarAdBlock.Domains.Count,
            LunarAdBlock.Domains.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    // --- the wire format of a refusal -------------------------------------------

    [Fact]
    public void AnAddressQuestionIsAnsweredWithAnUnroutableAddress()
    {
        var query = DnsMessage.BuildQuery(0x4242, "ads.overwolf.com", DnsRecordType.A);
        var response = DnsMessage.BuildSinkholeResponse(query, DnsMessage.SinkholeTtlSeconds);

        Assert.NotNull(response);

        // A well formed answer to this exact question, so the client accepts it rather
        // than treating it as a mismatch and asking somebody else.
        Assert.True(DnsMessage.IsResponseForQuery(query, response));
        Assert.Equal(0, DnsMessage.GetResponseCode(response));
        Assert.Equal(0x4242, DnsMessage.GetId(response));

        var answers = DnsMessage.ReadAnswers(response);
        Assert.Equal(DnsMessage.SinkholeTtlSeconds, Assert.Single(answers).Ttl);
        Assert.Equal(IPAddress.Any, Assert.Single(DnsMessage.ReadAddresses(response)));
    }

    [Fact]
    public void TheIpv6QuestionIsAnsweredTheSameWay()
    {
        var query = DnsMessage.BuildQuery(1, "tracking.overwolf.com", DnsRecordType.Aaaa);
        var response = DnsMessage.BuildSinkholeResponse(query, DnsMessage.SinkholeTtlSeconds);

        Assert.NotNull(response);
        Assert.True(DnsMessage.IsResponseForQuery(query, response));
        Assert.Equal(IPAddress.IPv6Any, Assert.Single(DnsMessage.ReadAddresses(response)));
    }

    /// <summary>
    /// Anything that is not an address question comes back as NODATA, not as an error.
    /// </summary>
    /// <remarks>
    /// Chromium asks for an HTTPS record before it connects to anything, and a SERVFAIL
    /// there is a reason to retry - which would turn a refusal into a real lookup. NODATA
    /// is final, and it is also true: the name has no record of that type here.
    /// </remarks>
    [Theory]
    [InlineData(DnsRecordType.Https)]
    [InlineData(DnsRecordType.Txt)]
    [InlineData(DnsRecordType.Cname)]
    public void AQuestionThatIsNotAnAddressComesBackEmptyRatherThanFailed(ushort type)
    {
        var query = DnsMessage.BuildQuery(7, "ads.overwolf.com", type);
        var response = DnsMessage.BuildSinkholeResponse(query, DnsMessage.SinkholeTtlSeconds);

        Assert.NotNull(response);
        Assert.True(DnsMessage.IsResponseForQuery(query, response));
        Assert.Equal(0, DnsMessage.GetResponseCode(response));
        Assert.Empty(DnsMessage.ReadAnswers(response));
    }

    /// <summary>Recursion-available is set and the client's own recursion-desired is kept.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheAnswerLooksLikeOneARecursiveResolverWouldSend(bool recursionDesired)
    {
        var query = DnsMessage.BuildQuery(9, "ads.overwolf.com", DnsRecordType.A, recursionDesired);
        var response = DnsMessage.BuildSinkholeResponse(query, DnsMessage.SinkholeTtlSeconds)!;
        var flags = BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(2));

        Assert.Equal(0x8000, flags & 0x8000);
        Assert.Equal(0x0080, flags & 0x0080);
        Assert.Equal(recursionDesired ? 0x0100 : 0, flags & 0x0100);
        Assert.Equal(0, flags & 0x0400);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(response.AsSpan(10)));
    }

    [Fact]
    public void AQuestionInAnotherClassIsLeftToBeResolved()
    {
        var query = DnsMessage.BuildQuery(3, "ads.overwolf.com", DnsRecordType.A);
        BinaryPrimitives.WriteUInt16BigEndian(query.AsSpan(query.Length - 2), 3); // CHAOS

        Assert.Null(DnsMessage.BuildSinkholeResponse(query, DnsMessage.SinkholeTtlSeconds));
    }

    [Fact]
    public void AMessageThatIsNotASingleQuestionIsLeftAlone()
    {
        Assert.Null(DnsMessage.BuildSinkholeResponse([], DnsMessage.SinkholeTtlSeconds));
        Assert.Null(DnsMessage.BuildSinkholeResponse(new byte[12], DnsMessage.SinkholeTtlSeconds));

        var twoQuestions = DnsMessage.BuildQuery(4, "ads.overwolf.com", DnsRecordType.A);
        BinaryPrimitives.WriteUInt16BigEndian(twoQuestions.AsSpan(4), 2);
        Assert.Null(DnsMessage.BuildSinkholeResponse(twoQuestions, DnsMessage.SinkholeTtlSeconds));
    }

    // --- the proxy, which is where the cost claim is decided ---------------------

    /// <summary>
    /// A blocked name is answered locally; an unblocked one is resolved as before.
    /// </summary>
    /// <remarks>
    /// The upstream request counter is the point of this test. Zero requests for the
    /// blocked name is what "does not slow the connection down" means in practice: no
    /// DoH round trip, no TCP connection, no packet.
    /// </remarks>
    [Fact]
    public async Task ABlockedNameNeverReachesTheResolver()
    {
        using var upstream = new CountingDns();
        using var resolver = new DohResolver(transport: upstream);
        await using var proxy = new DnsProxyServer(resolver) { Sinkhole = LunarAdBlock.Blocks };

        var blocked = DnsMessage.BuildQuery(1, "ads.overwolf.com", DnsRecordType.A);
        var allowed = DnsMessage.BuildQuery(2, "api.lunarclientprod.com", DnsRecordType.A);

        var refused = await proxy.ResolveAsync(blocked, default);
        Assert.NotNull(refused);
        Assert.Equal(IPAddress.Any, Assert.Single(DnsMessage.ReadAddresses(refused)));
        Assert.Equal(0, upstream.Requests);
        Assert.Equal(1, proxy.SinkholedAnswers);

        Assert.NotNull(await proxy.ResolveAsync(allowed, default));
        Assert.Equal(1, upstream.Requests);
        Assert.Equal(1, proxy.SinkholedAnswers);
    }

    /// <summary>
    /// Switching the block off frees the name at once, with nothing stale left behind.
    /// </summary>
    /// <remarks>
    /// The refusal is decided before the cache is read and is never written into it, so
    /// there is no sinkholed answer for a later lookup to find. That is what lets the
    /// switch take effect immediately instead of after a cache entry expires.
    /// </remarks>
    [Fact]
    public async Task TheSwitchTakesEffectOnTheNextLookupInBothDirections()
    {
        var enabled = false;
        using var upstream = new CountingDns();
        using var resolver = new DohResolver(transport: upstream);
        await using var proxy = new DnsProxyServer(resolver)
        {
            Sinkhole = name => enabled && LunarAdBlock.Blocks(name),
        };

        var query = DnsMessage.BuildQuery(1, "ads.overwolf.com", DnsRecordType.A);

        Assert.Equal(IPAddress.Parse("203.0.113.7"), Assert.Single(
            DnsMessage.ReadAddresses((await proxy.ResolveAsync(query, default))!)));

        enabled = true;
        Assert.Equal(IPAddress.Any, Assert.Single(
            DnsMessage.ReadAddresses((await proxy.ResolveAsync(query, default))!)));

        enabled = false;
        Assert.Equal(IPAddress.Parse("203.0.113.7"), Assert.Single(
            DnsMessage.ReadAddresses((await proxy.ResolveAsync(query, default))!)));
    }

    /// <summary>
    /// The block and Vodafone Sınırsız Modu's IPv6 rule do not interfere with each other.
    /// </summary>
    /// <remarks>
    /// Both intercept a lookup before it is resolved, so the order they are evaluated in
    /// is the whole question. The block goes first, which means a blocked name gets the
    /// same answer whether or not the hotspot rule is suppressing IPv6 under it, and a
    /// name that is not blocked still gets the hotspot rule's NODATA rather than a
    /// sinkhole address.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task VodafoneModesIpv6RuleAndTheBlockStayOutOfEachOthersWay(bool suppressIpv6)
    {
        using var upstream = new CountingDns();
        using var resolver = new DohResolver(transport: upstream);
        await using var proxy = new DnsProxyServer(resolver)
        {
            Sinkhole = LunarAdBlock.Blocks,
            SuppressIPv6Answers = () => suppressIpv6,
        };

        var blocked = await proxy.ResolveAsync(
            DnsMessage.BuildQuery(1, "ads.overwolf.com", DnsRecordType.Aaaa), default);

        Assert.NotNull(blocked);
        Assert.Equal(IPAddress.IPv6Any, Assert.Single(DnsMessage.ReadAddresses(blocked)));
        Assert.Equal(0, upstream.Requests);

        // The rewrite's own rule is untouched: an ordinary name still gets whatever the
        // hotspot policy says, and the block has not taken it over.
        var ordinary = await proxy.ResolveAsync(
            DnsMessage.BuildQuery(2, "mc.hypixel.net", DnsRecordType.Aaaa), default);

        Assert.NotNull(ordinary);
        Assert.Equal(suppressIpv6 ? 0 : 1, DnsMessage.ReadAddresses(ordinary).Count());
        Assert.Equal(suppressIpv6 ? 0 : 1, upstream.Requests);
    }

    /// <summary>
    /// The discovery pass is not told about names that were never looked up.
    /// </summary>
    /// <remarks>
    /// It exists to notice sites the operator is blocking and test them; handing it an
    /// advertising host we have just refused ourselves would have it probe a name nobody
    /// is trying to reach, and possibly learn it as a site worth protecting.
    /// </remarks>
    [Fact]
    public async Task TheDiscoveryPassIsNotToldAboutABlockedName()
    {
        var observed = new List<string>();
        using var upstream = new CountingDns();
        using var resolver = new DohResolver(transport: upstream);
        await using var proxy = new DnsProxyServer(resolver) { Sinkhole = LunarAdBlock.Blocks };
        proxy.NameResolved += name => observed.Add(name);

        await proxy.ResolveAsync(DnsMessage.BuildQuery(1, "ads.overwolf.com", DnsRecordType.A), default);
        await proxy.ResolveAsync(DnsMessage.BuildQuery(2, "api.lunarclientprod.com", DnsRecordType.A), default);

        Assert.Equal(["api.lunarclientprod.com"], observed);
    }

    /// <summary>Without the hook the proxy behaves exactly as it did before.</summary>
    [Fact]
    public async Task NothingChangesForAProxyWithNoBlockList()
    {
        using var upstream = new CountingDns();
        using var resolver = new DohResolver(transport: upstream);
        await using var proxy = new DnsProxyServer(resolver);

        var response = await proxy.ResolveAsync(
            DnsMessage.BuildQuery(1, "ads.overwolf.com", DnsRecordType.A), default);

        Assert.NotNull(response);
        Assert.Equal(IPAddress.Parse("203.0.113.7"), Assert.Single(DnsMessage.ReadAddresses(response)));
        Assert.Equal(1, upstream.Requests);
        Assert.Equal(0, proxy.SinkholedAnswers);
    }

    // --- the switch, end to end -------------------------------------------------

    /// <summary>
    /// Turning the switch on writes the setting and the hosts block; off removes both.
    /// </summary>
    /// <remarks>
    /// The resolver layer is not asserted here because it needs a running service to be
    /// running at all; what this covers is the half that persists on disk, and that the
    /// service leaves nothing behind when the user changes their mind.
    /// </remarks>
    [Fact]
    public async Task TheServiceSwitchWritesBothTheSettingAndTheHostsBlock()
    {
        using var directory = new TempDirectory("lunar-ads");
        var store = new ConfigStore(directory.File("settings.json"), directory.File("networks.json"));
        var hosts = directory.File("hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");

        await using var service = new ProtectionService(
            store,
            new LearnedDomainStore(directory.File("learned.json")),
            hostsBlocklist: new HostsFileBlocklist(hosts));

        Assert.False(service.Settings.BlockLunarAds);
        Assert.False(service.DescribeLunarAdBlock().Enabled);

        var enabled = await service.ApplyLunarAdBlockAsync(true);

        Assert.True(enabled.Enabled);
        Assert.True(enabled.HostsFileActive);
        Assert.True(store.Load().BlockLunarAds);
        Assert.Contains("0.0.0.0 ads.overwolf.com", File.ReadAllText(hosts), StringComparison.Ordinal);

        // The resolver layer is off because nothing is running, and the state says so
        // rather than claiming the whole feature is in place.
        Assert.False(enabled.ResolverActive);

        var disabled = await service.ApplyLunarAdBlockAsync(false);

        Assert.False(disabled.Enabled);
        Assert.False(disabled.HostsFileActive);
        Assert.False(store.Load().BlockLunarAds);
        Assert.Equal("127.0.0.1 localhost\r\n", File.ReadAllText(hosts));
    }

    /// <summary>
    /// A block the user asked for is put back if something removed it while we were away.
    /// </summary>
    [Fact]
    public async Task StartUpPutsBackABlockSomethingElseRemoved()
    {
        using var directory = new TempDirectory("lunar-ads");
        var store = new ConfigStore(directory.File("settings.json"), directory.File("networks.json"));
        var hosts = directory.File("hosts");
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");

        await using var service = new ProtectionService(
            store,
            new LearnedDomainStore(directory.File("learned.json")),
            hostsBlocklist: new HostsFileBlocklist(hosts));

        await service.ApplyLunarAdBlockAsync(true);
        File.WriteAllText(hosts, "127.0.0.1 localhost\r\n");
        Assert.DoesNotContain("ads.overwolf.com", File.ReadAllText(hosts), StringComparison.Ordinal);

        var state = service.SyncLunarAdBlock();

        Assert.True(state.HostsFileActive);
        Assert.Contains("0.0.0.0 ads.overwolf.com", File.ReadAllText(hosts), StringComparison.Ordinal);
    }

    /// <summary>The setting survives a round trip through the settings file.</summary>
    [Fact]
    public void TheSettingRoundTripsAndDefaultsToOff()
    {
        using var directory = new TempDirectory("lunar-ads");
        var store = new ConfigStore(directory.File("settings.json"), directory.File("networks.json"));

        Assert.False(new AppSettings().BlockLunarAds);
        Assert.False(store.Load().BlockLunarAds);

        Assert.True(store.Save(new AppSettings { BlockLunarAds = true }).Succeeded);
        Assert.True(store.Load().BlockLunarAds);
    }

    /// <summary>An upstream that answers every address question, and counts the asking.</summary>
    private sealed class CountingDns : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Requests++;
            var reply = await request.Content!.ReadAsByteArrayAsync(token);
            Assert.True(DnsMessage.TryReadQuestion(reply, out var question));

            reply[2] |= 0x80;
            reply[3] = 0x80;

            var address = question.Type switch
            {
                DnsRecordType.A => IPAddress.Parse("203.0.113.7"),
                DnsRecordType.Aaaa => IPAddress.Parse("2001:db8::7"),
                _ => null,
            };

            if (address is not null)
            {
                var payload = address.GetAddressBytes();
                var offset = reply.Length;
                Array.Resize(ref reply, offset + 12 + payload.Length);
                reply[7] = 1;
                reply[offset] = 0xC0;
                reply[offset + 1] = 12;
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(offset + 2), question.Type);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(offset + 4), 1);
                BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(offset + 6), 120);
                BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(offset + 10), (ushort)payload.Length);
                payload.CopyTo(reply, offset + 12);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(reply) };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            return response;
        }
    }
}
