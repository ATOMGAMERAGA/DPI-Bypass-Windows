using System.Net;
using System.Text.Json;
using DpiBypass.Core.Diagnostics;
using DpiBypass.Core.Engine;
using DpiBypass.Core.Ipc;
using DpiBypass.Core.Network;
using Xunit;

namespace DpiBypass.Tests;

public sealed class ProtectionWireJsonTests
{
    [Fact]
    public void EngineStatisticsRoundTripPreservesEveryCounterAndMetadata()
    {
        var statistics = new EngineStatistics { LastHost = "example.test", StartedAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        statistics.AddInspected(); statistics.AddRewritten(); statistics.AddSegments(3);
        statistics.AddDecoys(2); statistics.AddPassedThrough(); statistics.AddQuicBlocked(); statistics.AddError();
        var restored = RoundTrip(statistics);
        Assert.Equal(statistics.Inspected, restored.Inspected);
        Assert.Equal(statistics.Rewritten, restored.Rewritten);
        Assert.Equal(statistics.SegmentsSent, restored.SegmentsSent);
        Assert.Equal(statistics.DecoysSent, restored.DecoysSent);
        Assert.Equal(statistics.PassedThrough, restored.PassedThrough);
        Assert.Equal(statistics.QuicHandshakesBlocked, restored.QuicHandshakesBlocked);
        Assert.Equal(statistics.Errors, restored.Errors);
        Assert.Equal(statistics.LastHost, restored.LastHost);
        Assert.Equal(statistics.StartedAt, restored.StartedAt);
        Assert.True(restored.Uptime >= TimeSpan.FromSeconds(59));
    }

    [Fact]
    public void VerifyAllTuplesPreserveHostsAndProbeResults()
    {
        IReadOnlyList<(string Host, ProbeResult Result)> results =
            [("example.test", new(ProbeOutcome.Reachable, TimeSpan.FromMilliseconds(12), HttpStatus: 200))];
        var restored = RoundTrip(results);
        Assert.Equal(results[0].Host, restored[0].Host);
        Assert.Equal(results[0].Result, restored[0].Result);
    }

    [Theory]
    [InlineData("192.0.2.1")]
    [InlineData("2001:db8::1")]
    [InlineData("fe80::1%12")]
    public void AddressesAndEndpointsRoundTripAsStrings(string addressText)
    {
        var address = IPAddress.Parse(addressText);
        Assert.Equal(address, RoundTrip(address));
        var endpoint = new IPEndPoint(address, 443);
        Assert.Equal(endpoint, RoundTrip(endpoint));
        var detection = new IspDetection(IspCatalog.Unknown, address, null, null, null, true);
        Assert.Equal(address, RoundTrip(detection).PublicAddress);
        var candidate = new GameEndpointCandidate
        {
            Endpoint = new() { Address = address, Port = 443, Protocol = LatencyProtocol.Tcp, Label = "fixture", LocalEndpoint = endpoint },
            Score = 1, Why = "fixture",
        };
        Assert.Equal(endpoint, RoundTrip(candidate).Endpoint.LocalEndpoint);
    }

    [Theory]
    [InlineData("123")]
    [InlineData("\"123\"")]
    [InlineData("\"NotARealOutcome\"")]
    public void UnknownAndNumericEnumsAreRejected(string json)
        => Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProbeOutcome>(json, ProtectionWireJson.Options));

    [Fact]
    public void UnknownMembersMissingCountersAndNonfiniteNumbersAreRejected()
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ProbeResult>(
            "{\"Outcome\":\"Reachable\",\"Elapsed\":\"00:00:00\",\"Unexpected\":true}", ProtectionWireJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EngineStatistics>("{}", ProtectionWireJson.Options));
        var json = JsonSerializer.Serialize(new EngineStatistics(), ProtectionWireJson.Options);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EngineStatistics>(json.Replace("\"Inspected\":0", "\"Inspected\":-1"), ProtectionWireJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<EngineStatistics>(json[..^1] + ",\"Unexpected\":true}", ProtectionWireJson.Options));
        Assert.Throws<ArgumentException>(() => JsonSerializer.Serialize(double.NaN, ProtectionWireJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<double>("\"Infinity\"", ProtectionWireJson.Options));
    }

    [Theory]
    [InlineData("\"example.test\"")]
    [InlineData("42")]
    [InlineData("{}")]
    public void InvalidNetworkAddressesAreRejected(string json)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IPAddress>(json, ProtectionWireJson.Options));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<IPEndPoint>(json, ProtectionWireJson.Options));
    }

    private static T RoundTrip<T>(T value)
        => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, ProtectionWireJson.Options), ProtectionWireJson.Options)!;
}
