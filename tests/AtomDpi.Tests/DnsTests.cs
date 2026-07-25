using System.Buffers.Binary;
using System.Net;
using System.Text;
using AtomDpi.Core.Dns;
using Xunit;

namespace AtomDpi.Tests;

public class DnsMessageTests
{
    [Theory]
    [InlineData("discord.com", DnsRecordType.A)]
    [InlineData("gateway.discord.gg", DnsRecordType.Aaaa)]
    [InlineData("1.1.1.1.origin.asn.cymru.com", DnsRecordType.Txt)]
    public void QueryRoundTripsThroughTheQuestionReader(string name, ushort type)
    {
        var query = DnsMessage.BuildQuery(0x1234, name, type);

        Assert.Equal(0x1234, DnsMessage.GetId(query));
        Assert.True(DnsMessage.TryReadQuestion(query, out var question));
        Assert.Equal(name, question.Name);
        Assert.Equal(type, question.Type);
        Assert.Equal(1, question.Class);
    }

    [Fact]
    public void CacheKeyDistinguishesNameTypeAndClass()
    {
        var a = new DnsQuestion("discord.com", DnsRecordType.A, 1);
        var aaaa = new DnsQuestion("discord.com", DnsRecordType.Aaaa, 1);
        var upper = new DnsQuestion("DISCORD.COM", DnsRecordType.A, 1);

        Assert.NotEqual(a.CacheKey, aaaa.CacheKey);
        Assert.Equal(a.CacheKey, upper.CacheKey);
    }

    [Fact]
    public void IdCanBeRewrittenWithoutDisturbingTheRest()
    {
        var query = DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A);
        DnsMessage.SetId(query, 0xBEEF);

        Assert.Equal(0xBEEF, DnsMessage.GetId(query));
        Assert.True(DnsMessage.TryReadQuestion(query, out var question));
        Assert.Equal("discord.com", question.Name);
    }

    [Fact]
    public void TrailingDotIsNormalisedAway()
    {
        var query = DnsMessage.BuildQuery(1, "discord.com.", DnsRecordType.A);
        Assert.True(DnsMessage.TryReadQuestion(query, out var question));
        Assert.Equal("discord.com", question.Name);
    }

    [Fact]
    public void ReadsAddressesFromAResponse()
    {
        var response = BuildResponse(
            "discord.com",
            (DnsRecordType.A, 120u, new byte[] { 162, 159, 128, 233 }),
            (DnsRecordType.A, 300u, new byte[] { 162, 159, 135, 232 }));

        var addresses = DnsMessage.ReadAddresses(response).ToList();

        Assert.Equal(2, addresses.Count);
        Assert.Equal(IPAddress.Parse("162.159.128.233"), addresses[0]);
        Assert.Equal(IPAddress.Parse("162.159.135.232"), addresses[1]);
    }

    [Fact]
    public void MinimumTtlIsTheSmallestAnswerTtlWithinBounds()
    {
        var response = BuildResponse(
            "discord.com",
            (DnsRecordType.A, 900u, new byte[] { 1, 2, 3, 4 }),
            (DnsRecordType.A, 120u, new byte[] { 5, 6, 7, 8 }));

        Assert.Equal(120u, DnsMessage.GetMinimumTtl(response));
    }

    [Fact]
    public void MinimumTtlIsClampedSoTheCacheNeverPinsOrThrashes()
    {
        var tiny = BuildResponse("discord.com", (DnsRecordType.A, 1u, new byte[] { 1, 2, 3, 4 }));
        var huge = BuildResponse("discord.com", (DnsRecordType.A, 86400u, new byte[] { 1, 2, 3, 4 }));

        Assert.Equal(30u, DnsMessage.GetMinimumTtl(tiny));
        Assert.Equal(3600u, DnsMessage.GetMinimumTtl(huge));
    }

    [Fact]
    public void AnEmptyAnswerSectionFallsBackToTheTtlFloor()
    {
        var response = BuildResponse("discord.com");
        Assert.Equal(30u, DnsMessage.GetMinimumTtl(response));
    }

    [Fact]
    public void ReadsAPointerRecordThroughNameCompression()
    {
        var response = BuildResponse(
            "233.128.159.162.in-addr.arpa",
            (DnsRecordType.Ptr, 3600u, DnsMessage.EncodeName("host.ttnet.com.tr")));

        Assert.Equal("host.ttnet.com.tr", DnsMessage.ReadFirstPointer(response));
    }

    [Fact]
    public void ResponseCodeIsExtracted()
    {
        var ok = BuildResponse("discord.com", (DnsRecordType.A, 60u, new byte[] { 1, 2, 3, 4 }));
        Assert.Equal(0, DnsMessage.GetResponseCode(ok));

        ok[3] = (byte)((ok[3] & 0xF0) | 3); // NXDOMAIN
        Assert.Equal(3, DnsMessage.GetResponseCode(ok));
    }

    [Fact]
    public void DecodesCompressedNames()
    {
        // Question "discord.com", then an answer whose owner name is a pointer to it.
        var message = new List<byte>();
        message.AddRange(new byte[] { 0, 1, 0x81, 0x80, 0, 1, 0, 1, 0, 0, 0, 0 });
        var offsetOfName = message.Count;
        message.AddRange(DnsMessage.EncodeName("discord.com"));
        message.AddRange([0, 1, 0, 1]);

        message.AddRange([(byte)(0xC0 | (offsetOfName >> 8)), (byte)offsetOfName]);
        message.AddRange([0, 1, 0, 1, 0, 0, 0, 60, 0, 4, 9, 9, 9, 9]);

        var answers = DnsMessage.ReadAnswers([.. message]);

        Assert.Single(answers);
        Assert.Equal("discord.com", answers[0].Name);
        Assert.Equal(IPAddress.Parse("9.9.9.9"), new IPAddress(answers[0].Data));
    }

    [Fact]
    public void SurvivesTruncatedAndMalformedMessages()
    {
        Assert.False(DnsMessage.TryReadQuestion([], out _));
        Assert.False(DnsMessage.TryReadQuestion(new byte[5], out _));
        Assert.Empty(DnsMessage.ReadAnswers(new byte[8]));

        var query = DnsMessage.BuildQuery(1, "discord.com", DnsRecordType.A);
        Assert.Empty(DnsMessage.ReadAnswers(query[..(query.Length - 3)]));
    }

    [Fact]
    public void CompressionLoopsAreRejectedRatherThanHanging()
    {
        // A pointer that points at itself would spin forever without a hop limit.
        var message = new byte[] { 0, 1, 0x81, 0x80, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 0x0C };
        var pos = 12;
        Assert.False(DnsMessage.TryReadName(message, ref pos, out _));
    }

    [Theory]
    [InlineData("162.159.128.233", "233.128.159.162.in-addr.arpa")]
    [InlineData("8.8.8.8", "8.8.8.8.in-addr.arpa")]
    public void BuildsReverseNamesForIPv4(string address, string expected)
        => Assert.Equal(expected, DnsMessage.ToReverseName(IPAddress.Parse(address)));

    [Fact]
    public void BuildsReverseNamesForIPv6()
    {
        var name = DnsMessage.ToReverseName(IPAddress.Parse("2606:4700:4700::1111"));

        Assert.EndsWith("ip6.arpa", name);
        Assert.StartsWith("1.1.1.1.0.0.0.0", name);
        Assert.Equal(32, name[..name.IndexOf("ip6.arpa", StringComparison.Ordinal)].Count(c => c == '.'));
    }

    [Fact]
    public void RejectsOverlongLabels()
        => Assert.Throws<ArgumentException>(() => DnsMessage.EncodeName(new string('a', 64) + ".com"));

    private static byte[] BuildResponse(string name, params (ushort Type, uint Ttl, byte[] Data)[] answers)
    {
        var message = new List<byte>();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, 0x4321);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x8180);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6), (ushort)answers.Length);
        message.AddRange(header);

        var encodedName = DnsMessage.EncodeName(name);
        message.AddRange(encodedName);
        message.AddRange([0, 1, 0, 1]);

        foreach (var (type, ttl, data) in answers)
        {
            message.AddRange(encodedName);
            var fixedPart = new byte[10];
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart, type);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(2), 1);
            BinaryPrimitives.WriteUInt32BigEndian(fixedPart.AsSpan(4), ttl);
            BinaryPrimitives.WriteUInt16BigEndian(fixedPart.AsSpan(8), (ushort)data.Length);
            message.AddRange(fixedPart);
            message.AddRange(data);
        }

        return [.. message];
    }
}

public class DohEndpointTests
{
    [Fact]
    public void CloudflareLeadsAndTheFallbacksFollowInOrder()
    {
        var providers = DohResolver.DefaultChain.Select(e => e.Provider).Distinct().ToList();

        Assert.Equal(["Cloudflare", "Google", "Quad9"], providers);
    }

    [Fact]
    public void EveryEndpointIsAddressedByIpLiteralSoNoSniIsSent()
    {
        foreach (var endpoint in DohResolver.DefaultChain)
        {
            var uri = new Uri(endpoint.Url);

            // An IP literal host means TLS omits SNI entirely, which is what makes
            // encrypted DNS survive an SNI filter.
            Assert.True(
                uri.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6,
                $"{endpoint.Url} must not use a hostname");
            Assert.Equal(endpoint.PlainAddress.ToString(), uri.Host);
            Assert.Equal("/dns-query", uri.AbsolutePath);
        }
    }

    [Fact]
    public void EachProviderContributesTwoAddresses()
    {
        foreach (var group in DohResolver.DefaultChain.GroupBy(e => e.Provider))
        {
            Assert.Equal(2, group.Count());
        }
    }
}

public class DnsConfiguratorTests
{
    [Fact]
    public void PublicResolverListsLeadWithCloudflareAndCoverAllThreeProviders()
    {
        Assert.Equal("1.1.1.1", DnsConfigurator.PublicV4[0]);
        Assert.Contains("8.8.8.8", DnsConfigurator.PublicV4);
        Assert.Contains("9.9.9.9", DnsConfigurator.PublicV4);

        Assert.All(DnsConfigurator.PublicV4, address => Assert.True(IPAddress.TryParse(address, out _)));
        Assert.All(DnsConfigurator.PublicV6, address => Assert.True(IPAddress.TryParse(address, out _)));
    }

    [Fact]
    public void RestoreIsAskedForOnlyWhenASnapshotExists()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"atomdpi-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            var configurator = new DnsConfigurator(directory);
            Assert.False(configurator.HasPendingRestore);

            File.WriteAllText(Path.Combine(directory, "dns-snapshot.json"), "[]");
            Assert.True(new DnsConfigurator(directory).HasPendingRestore);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

public class FakePayloadTests
{
    [Fact]
    public void DecoyHostsAreOrdinaryLookingAndNotDiscord()
    {
        Assert.NotEmpty(AtomDpi.Core.Engine.FakePayloadFactory.DecoyHosts);

        foreach (var host in AtomDpi.Core.Engine.FakePayloadFactory.DecoyHosts)
        {
            Assert.DoesNotContain("discord", host, StringComparison.OrdinalIgnoreCase);
            Assert.Contains('.', host);
        }
    }

    [Fact]
    public void DecoyHttpRequestCarriesTheHostHeader()
    {
        var request = AtomDpi.Core.Engine.FakePayloadFactory.CreateHttpRequest("www.microsoft.com");
        var text = Encoding.ASCII.GetString(request);

        Assert.StartsWith("GET / HTTP/1.1\r\n", text);
        Assert.Contains("Host: www.microsoft.com\r\n", text);
        Assert.EndsWith("\r\n\r\n", text);
    }

    [Fact]
    public void DecoyHttpRequestCanBePaddedToLength()
    {
        var request = AtomDpi.Core.Engine.FakePayloadFactory.CreateHttpRequest("www.bing.com", minimumLength: 400);
        Assert.True(request.Length >= 400);
    }

    [Fact]
    public void DecoysAreDeterministicSoBehaviourIsReproducible()
    {
        var first = AtomDpi.Core.Engine.FakePayloadFactory.CreateTlsClientHello("www.bing.com");
        var second = AtomDpi.Core.Engine.FakePayloadFactory.CreateTlsClientHello("www.bing.com");

        Assert.Equal(first, second);
    }
}
