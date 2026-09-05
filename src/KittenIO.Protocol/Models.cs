using System.Text.Json;
using System.Text.Json.Serialization;

namespace KittenIO.Protocol;

[JsonConverter(typeof(AxisCommandJsonConverter))]
public enum AxisCommand
{
    Negative = -1,
    Neutral = 0,
    Positive = 1,
}

public sealed class AxisCommandJsonConverter : JsonConverter<AxisCommand>
{
    public override AxisCommand Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Axis commands must be strings: negative, neutral, or positive.");
        }

        return reader.GetString() switch
        {
            "negative" => AxisCommand.Negative,
            "neutral" => AxisCommand.Neutral,
            "positive" => AxisCommand.Positive,
            _ => throw new JsonException("Axis commands must be negative, neutral, or positive."),
        };
    }

    public override void Write(Utf8JsonWriter writer, AxisCommand value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value switch
        {
            AxisCommand.Negative => "negative",
            AxisCommand.Neutral => "neutral",
            AxisCommand.Positive => "positive",
            _ => throw new JsonException("Axis commands must be negative, neutral, or positive."),
        });
    }
}

public sealed record HelloPayload
{
    [JsonPropertyName("protocol")]
    public string Protocol { get; init; } = ProtocolConstants.Name;

    [JsonPropertyName("supported_versions")]
    public IReadOnlyList<int> SupportedVersions { get; init; } = ProtocolConstants.SupportedVersions;

    [JsonPropertyName("mod_version")]
    public required string ModVersion { get; init; }

    [JsonPropertyName("game_version")]
    public required string GameVersion { get; init; }

    [JsonPropertyName("starmap_version")]
    public required string StarMapVersion { get; init; }

    [JsonPropertyName("capabilities")]
    public required CapabilitySet Capabilities { get; init; }

    [JsonPropertyName("units")]
    public string Units { get; init; } = "SI";

    [JsonPropertyName("coordinate_frames")]
    public IReadOnlyList<string> CoordinateFrames { get; init; } = ["cci", "cce", "orb"];

}

public sealed record WelcomePayload
{
    [JsonPropertyName("selected_version")]
    public int SelectedVersion { get; init; } = ProtocolConstants.Version;

    [JsonPropertyName("session_id")]
    public required Guid SessionId { get; init; }

    [JsonPropertyName("sdk_version")]
    public required string SdkVersion { get; init; }

    [JsonPropertyName("telemetry_hz")]
    public int TelemetryHz { get; init; } = ProtocolConstants.DefaultTelemetryHz;
}

public readonly record struct Vector3Value(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z);

public readonly record struct QuaternionValue(
    [property: JsonPropertyName("x")] double X,
    [property: JsonPropertyName("y")] double Y,
    [property: JsonPropertyName("z")] double Z,
    [property: JsonPropertyName("w")] double W);

public sealed record OrbitState
{
    [JsonPropertyName("eccentricity")]
    public double? Eccentricity { get; init; }

    [JsonPropertyName("semi_major_axis_m")]
    public double? SemiMajorAxisM { get; init; }

    [JsonPropertyName("inclination_rad")]
    public double? InclinationRad { get; init; }

    [JsonPropertyName("longitude_ascending_node_rad")]
    public double? LongitudeAscendingNodeRad { get; init; }

    [JsonPropertyName("argument_periapsis_rad")]
    public double? ArgumentPeriapsisRad { get; init; }

    [JsonPropertyName("periapsis_m")]
    public double? PeriapsisM { get; init; }

    [JsonPropertyName("apoapsis_m")]
    public double? ApoapsisM { get; init; }

    [JsonPropertyName("period_s")]
    public double? PeriodS { get; init; }
}

public sealed record TargetState
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }

    [JsonPropertyName("position_cci_m")]
    public Vector3Value? PositionCciM { get; init; }

    [JsonPropertyName("velocity_cci_mps")]
    public Vector3Value? VelocityCciMps { get; init; }

    [JsonPropertyName("orbit")]
    public OrbitState? Orbit { get; init; }
}

public sealed record ManualControlState
{
    [JsonPropertyName("engine_on")]
    public bool? EngineOn { get; init; }

    [JsonPropertyName("throttle")]
    public double? Throttle { get; init; }

    [JsonPropertyName("pitch")]
    public AxisCommand? Pitch { get; init; }

    [JsonPropertyName("yaw")]
    public AxisCommand? Yaw { get; init; }

    [JsonPropertyName("roll")]
    public AxisCommand? Roll { get; init; }

    [JsonPropertyName("translate_forward")]
    public AxisCommand? TranslateForward { get; init; }

    [JsonPropertyName("translate_right")]
    public AxisCommand? TranslateRight { get; init; }

    [JsonPropertyName("translate_up")]
    public AxisCommand? TranslateUp { get; init; }
}

public sealed record EntityReference
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("kind")]
    public required string Kind { get; init; }
}

public sealed record CatalogPayload
{
    [JsonPropertyName("bodies")]
    public IReadOnlyList<EntityReference> Bodies { get; init; } = [];

    [JsonPropertyName("vehicles")]
    public IReadOnlyList<EntityReference> Vehicles { get; init; } = [];
}

public sealed record LeaseAcquirePayload
{
    [JsonPropertyName("vehicle_id")]
    public required string VehicleId { get; init; }
}

public sealed record LeaseGrantedPayload
{
    [JsonPropertyName("lease_id")]
    public required Guid LeaseId { get; init; }

    [JsonPropertyName("vehicle_id")]
    public required string VehicleId { get; init; }

    [JsonPropertyName("timeout_ms")]
    public int TimeoutMs { get; init; } = ProtocolConstants.DefaultControlTimeoutMs;
}

public sealed record LeaseReleasePayload
{
    [JsonPropertyName("lease_id")]
    public required Guid LeaseId { get; init; }

    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed record RequestPayload
{
    [JsonPropertyName("method")]
    public required string Method { get; init; }

    [JsonPropertyName("command_id")]
    public required Guid CommandId { get; init; }

    [JsonPropertyName("params")]
    public required JsonElement Parameters { get; init; }
}

public sealed record ResponsePayload
{
    [JsonPropertyName("ok")]
    public required bool Ok { get; init; }

    [JsonPropertyName("result")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public object? Result { get; init; }

    [JsonPropertyName("error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ErrorPayload? Error { get; init; }
}

public sealed record ErrorPayload
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }
}

public sealed record EventPayload
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("data")]
    public required object Data { get; init; }
}

public sealed record HeartbeatPayload
{
    [JsonPropertyName("nonce")]
    public required string Nonce { get; init; }
}
