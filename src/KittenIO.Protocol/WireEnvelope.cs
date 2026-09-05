using System.Text.Json;
using System.Text.Json.Serialization;

namespace KittenIO.Protocol;

public sealed record WireEnvelope
{
    [JsonPropertyName("version")]
    public required int Version { get; init; }

    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("game_instance_id")]
    public required Guid GameInstanceId { get; init; }

    [JsonPropertyName("session_id")]
    public Guid? SessionId { get; init; }

    [JsonPropertyName("seq")]
    public required long Sequence { get; init; }

    [JsonPropertyName("sent_monotonic_ms")]
    public required long SentMonotonicMs { get; init; }

    [JsonPropertyName("request_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RequestId { get; init; }

    [JsonPropertyName("payload")]
    public required JsonElement Payload { get; init; }
}

public readonly record struct ProtocolError(string Code, string Message);
