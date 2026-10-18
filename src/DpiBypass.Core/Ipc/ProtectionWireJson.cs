using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DpiBypass.Core.Engine;

namespace DpiBypass.Core.Ipc;

/// <summary>The JSON contract shared by the user process and the privileged helper.</summary>
public static class ProtectionWireJson
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            MaxDepth = 64,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            NumberHandling = JsonNumberHandling.Strict,
        };
        options.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false));
        options.Converters.Add(new AddressConverter());
        options.Converters.Add(new EndpointConverter());
        options.Converters.Add(new StatisticsConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }

    private sealed class AddressConverter : JsonConverter<IPAddress>
    {
        public override IPAddress Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || !IPAddress.TryParse(reader.GetString(), out var address))
                throw new JsonException("Expected an IP address string.");
            return address;
        }

        public override void Write(Utf8JsonWriter writer, IPAddress value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    private sealed class EndpointConverter : JsonConverter<IPEndPoint>
    {
        public override IPEndPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String || reader.GetString() is not { } text
                || !IPEndPoint.TryParse(text, out var endpoint))
                throw new JsonException("Expected an IP endpoint string.");
            return endpoint;
        }

        public override void Write(Utf8JsonWriter writer, IPEndPoint value, JsonSerializerOptions options)
            => writer.WriteStringValue(value.ToString());
    }

    private sealed class StatisticsConverter : JsonConverter<EngineStatistics>
    {
        public override EngineStatistics Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var snapshot = JsonSerializer.Deserialize<StatisticsPayload>(ref reader, options)
                ?? throw new JsonException("Expected engine statistics.");
            if (snapshot.Inspected < 0 || snapshot.Rewritten < 0 || snapshot.SegmentsSent < 0
                || snapshot.DecoysSent < 0 || snapshot.PassedThrough < 0
                || snapshot.QuicHandshakesBlocked < 0 || snapshot.Errors < 0)
                throw new JsonException("Engine counters cannot be negative.");
            return EngineStatistics.FromSnapshot(snapshot.Inspected, snapshot.Rewritten,
                snapshot.SegmentsSent, snapshot.DecoysSent, snapshot.PassedThrough,
                snapshot.QuicHandshakesBlocked, snapshot.Errors, snapshot.LastHost, snapshot.StartedAt);
        }

        public override void Write(Utf8JsonWriter writer, EngineStatistics value, JsonSerializerOptions options)
            => JsonSerializer.Serialize(writer, new StatisticsPayload
            {
                Inspected = value.Inspected, Rewritten = value.Rewritten, SegmentsSent = value.SegmentsSent,
                DecoysSent = value.DecoysSent, PassedThrough = value.PassedThrough,
                QuicHandshakesBlocked = value.QuicHandshakesBlocked, Errors = value.Errors,
                LastHost = value.LastHost, StartedAt = value.StartedAt,
            }, options);
    }

    private sealed record StatisticsPayload
    {
        public required long Inspected { get; init; }
        public required long Rewritten { get; init; }
        public required long SegmentsSent { get; init; }
        public required long DecoysSent { get; init; }
        public required long PassedThrough { get; init; }
        public required long QuicHandshakesBlocked { get; init; }
        public required long Errors { get; init; }
        public required string? LastHost { get; init; }
        public required DateTimeOffset? StartedAt { get; init; }
    }
}
