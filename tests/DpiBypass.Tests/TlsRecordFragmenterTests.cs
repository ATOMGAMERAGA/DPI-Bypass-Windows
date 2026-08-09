using System.Buffers.Binary;
using System.Text;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Net;
using Xunit;

namespace DpiBypass.Tests;

public class TlsRecordFragmenterTests
{
    private static byte[] Hello(string host = "discord.com") => FakePayloadFactory.CreateTlsClientHello(host);

    /// <summary>Walks the record layer and returns each record's body.</summary>
    private static List<byte[]> ReadRecords(byte[] payload)
    {
        var records = new List<byte[]>();
        var pos = 0;

        while (pos + 5 <= payload.Length)
        {
            Assert.Equal(0x16, payload[pos]);
            var length = BinaryPrimitives.ReadUInt16BigEndian(payload.AsSpan(pos + 3));
            Assert.True(pos + 5 + length <= payload.Length);

            records.Add(payload.Skip(pos + 5).Take(length).ToArray());
            pos += 5 + length;
        }

        Assert.Equal(payload.Length, pos);
        return records;
    }

    [Fact]
    public void SplittingProducesTwoValidRecordsCarryingTheSameHandshake()
    {
        var payload = Hello();
        var original = ReadRecords(payload);
        Assert.Single(original);

        var split = TlsRecordFragmenter.Split(payload, original[0].Length / 2);

        Assert.NotNull(split);
        var records = ReadRecords(split!);
        Assert.Equal(2, records.Count);
        Assert.Equal(original[0], records.SelectMany(r => r).ToArray());
        Assert.Equal(payload.Length + 5, split!.Length);
    }

    [Fact]
    public void RecordVersionBytesAreCarriedOverUnchanged()
    {
        var payload = Hello();
        var split = TlsRecordFragmenter.Split(payload, 20);

        Assert.NotNull(split);
        Assert.Equal(payload[1], split![1]);
        Assert.Equal(payload[2], split[2]);
        Assert.Equal(payload[1], split[5 + BinaryPrimitives.ReadUInt16BigEndian(split.AsSpan(3)) + 1]);
    }

    [Fact]
    public void SplittingAtSeveralPointsProducesOneRecordPerCut()
    {
        var payload = Hello();
        var bodyLength = ReadRecords(payload)[0].Length;

        var split = TlsRecordFragmenter.Split(payload, [10, bodyLength / 2, bodyLength - 10]);

        Assert.NotNull(split);
        Assert.Equal(4, ReadRecords(split!).Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void OffsetsOutsideTheBodyAreRefused(int offset)
        => Assert.Null(TlsRecordFragmenter.Split(Hello(), offset));

    [Fact]
    public void AnOffsetPastTheEndOfTheBodyIsRefused()
    {
        var payload = Hello();
        Assert.Null(TlsRecordFragmenter.Split(payload, payload.Length + 100));
    }

    [Fact]
    public void DuplicateCutsCollapseIntoOne()
    {
        var payload = Hello();
        var once = TlsRecordFragmenter.Split(payload, [30]);
        var twice = TlsRecordFragmenter.Split(payload, [30, 30]);

        Assert.NotNull(once);
        Assert.Equal(once, twice);
    }

    [Fact]
    public void NonHandshakeTrafficIsLeftAlone()
    {
        var applicationData = new byte[64];
        applicationData[0] = 0x17; // application data, not handshake
        applicationData[1] = 0x03;
        applicationData[2] = 0x03;
        BinaryPrimitives.WriteUInt16BigEndian(applicationData.AsSpan(3), 59);

        Assert.Null(TlsRecordFragmenter.Split(applicationData, 10));
    }

    /// <summary>
    /// A ClientHello the client already split across TCP segments arrives as a partial
    /// record. Re-framing it would corrupt the handshake, so it must be refused.
    /// </summary>
    [Fact]
    public void AnIncompleteRecordIsRefused()
    {
        var payload = Hello();
        var truncated = payload.Take(payload.Length - 10).ToArray();

        Assert.Null(TlsRecordFragmenter.Split(truncated, 20));
    }

    [Fact]
    public void PlannerCutsTheRecordInsideTheHostname()
    {
        var strategy = StrategyLibrary.TlsRecordSni;
        var payload = Hello("discord.com");

        var plan = DesyncPlanner.Plan(strategy, payload, isTls: true, hostName: "discord.com");
        var records = ReadRecords(plan.Payload);

        Assert.Equal(2, records.Count);

        // Neither record may contain the whole hostname - that is the entire point.
        foreach (var record in records)
        {
            Assert.DoesNotContain("discord.com", Encoding.ASCII.GetString(record), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void PlaintextHttpIsNeverRecordFragmented()
    {
        var request = PacketFactory.HttpRequest("discord.com");
        var plan = DesyncPlanner.Plan(StrategyLibrary.TlsRecordSni, request, isTls: false, hostName: "discord.com");

        Assert.Equal(request, plan.Payload);
    }

    [Fact]
    public void RecordFragmentingStrategiesAreNotTreatedAsPassthrough()
    {
        Assert.False(StrategyLibrary.TlsRecordSni.IsPassthrough);
        Assert.True(StrategyLibrary.Passthrough.IsPassthrough);
    }
}
