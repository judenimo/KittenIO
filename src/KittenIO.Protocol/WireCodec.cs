using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace KittenIO.Protocol;

public static class WireCodec
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize<TPayload>(
        string type,
        Guid gameInstanceId,
        Guid? sessionId,
        long sequence,
        long sentMonotonicMs,
        TPayload payload,
        string? requestId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        if (gameInstanceId == Guid.Empty)
        {
            throw new ArgumentException("A non-empty game instance ID is required.", nameof(gameInstanceId));
        }

        if (sequence < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence));
        }

        if (sentMonotonicMs < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sentMonotonicMs));
        }

        var envelope = new WireEnvelope
        {
            Version = ProtocolConstants.Version,
            Type = type,
            GameInstanceId = gameInstanceId,
            SessionId = sessionId,
            Sequence = sequence,
            SentMonotonicMs = sentMonotonicMs,
            RequestId = requestId,
            Payload = payload is null
                ? throw new ArgumentNullException(nameof(payload))
                : JsonSerializer.SerializeToElement(payload, payload.GetType(), Options),
        };

        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        if (bytes.Length > ProtocolConstants.MaximumMessageBytes)
        {
            throw new InvalidOperationException($"Serialized message exceeds {ProtocolConstants.MaximumMessageBytes} bytes.");
        }

        return bytes;
    }

    public static bool TryDeserialize(
        ReadOnlySpan<byte> utf8Json,
        out WireEnvelope? envelope,
        out ProtocolError? error)
    {
        envelope = null;
        error = null;

        if (utf8Json.Length == 0)
        {
            error = new("empty_message", "The WebSocket message was empty.");
            return false;
        }

        if (utf8Json.Length > ProtocolConstants.MaximumMessageBytes)
        {
            error = new("message_too_large", $"The message exceeds {ProtocolConstants.MaximumMessageBytes} bytes.");
            return false;
        }

        try
        {
            envelope = JsonSerializer.Deserialize<WireEnvelope>(utf8Json, Options);
        }
        catch (JsonException exception)
        {
            error = new("invalid_json", exception.Message);
            return false;
        }

        if (envelope is null)
        {
            error = new("invalid_envelope", "The message did not contain an envelope.");
            return false;
        }

        return TryValidateEnvelope(envelope, out error);
    }

    public static bool TryReadPayload<T>(
        WireEnvelope envelope,
        out T? payload,
        out ProtocolError? error)
    {
        payload = default;
        error = null;

        try
        {
            payload = envelope.Payload.Deserialize<T>(Options);
        }
        catch (JsonException exception)
        {
            error = new("invalid_payload", exception.Message);
            return false;
        }

        if (payload is null)
        {
            error = new("invalid_payload", "The payload was null.");
            return false;
        }

        return true;
    }

    public static bool TryValidateControl(ControlFramePayload control, out ProtocolError? error)
    {
        error = null;
        if (control.LeaseId == Guid.Empty)
        {
            error = new("invalid_lease", "lease_id must be non-empty.");
            return false;
        }
        if (control.ControlSequence < 0)
        {
            error = new("invalid_sequence", "control_seq must be non-negative.");
            return false;
        }
        if (control.Throttle is { } throttle && (!double.IsFinite(throttle) || throttle is < 0 or > 1))
        {
            error = new("invalid_throttle", "throttle must be finite and within [0, 1].");
            return false;
        }
        if (!StringComparer.Ordinal.Equals(control.Mode, ControlModes.ThrustVectorTarget) ||
            control.ThrustVectorTarget is null)
        {
            error = new("unsupported_control_mode", "This build supports only thrust_vector_target control frames.");
            return false;
        }

        var direction = control.ThrustVectorTarget.DirectionCci;
        if (!double.IsFinite(direction.X) || !double.IsFinite(direction.Y) || !double.IsFinite(direction.Z))
        {
            error = new("invalid_thrust_vector", "direction_cci must contain finite components.");
            return false;
        }
        var normSquared = direction.X * direction.X + direction.Y * direction.Y + direction.Z * direction.Z;
        if (!double.IsFinite(normSquared) || Math.Abs(normSquared - 1d) > 1e-6)
        {
            error = new("invalid_thrust_vector", "direction_cci must be normalized within 1e-6 squared-norm tolerance.");
            return false;
        }
        return true;
    }

    private static bool TryValidateEnvelope(WireEnvelope envelope, out ProtocolError? error)
    {
        error = null;
        if (envelope.Version != ProtocolConstants.Version)
        {
            error = new("unsupported_version", $"Protocol version {envelope.Version} is not supported.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(envelope.Type))
        {
            error = new("invalid_type", "type must be non-empty.");
            return false;
        }

        if (envelope.GameInstanceId == Guid.Empty)
        {
            error = new("invalid_game_instance", "game_instance_id must be non-empty.");
            return false;
        }

        if (envelope.Type == ProtocolConstants.MessageTypes.Hello)
        {
            if (envelope.SessionId is not null)
            {
                error = new("invalid_session", "hello must use a null session_id.");
                return false;
            }
        }
        else if (envelope.SessionId is null || envelope.SessionId == Guid.Empty)
        {
            error = new("invalid_session", "session_id must be a non-empty UUID after hello.");
            return false;
        }

        if (envelope.Sequence < 0 || envelope.SentMonotonicMs < 0)
        {
            error = new("invalid_sequence", "Sequence and monotonic time must be non-negative.");
            return false;
        }

        if (envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            error = new("invalid_payload", "payload must be present.");
            return false;
        }

        return true;
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            PropertyNameCaseInsensitive = false,
            AllowDuplicateProperties = false,
            NumberHandling = JsonNumberHandling.Strict,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            AllowTrailingCommas = false,
            ReadCommentHandling = JsonCommentHandling.Disallow,
        };
        options.Converters.Add(new CanonicalGuidJsonConverter());
        options.TypeInfoResolverChain.Insert(0, KittenIoJsonContext.Default);
        // Source-generated metadata covers every canonical DTO. The fallback is retained for
        // adapter-produced command result objects before they are wrapped as JsonElement.
        options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        return options;
    }
}

internal sealed class CanonicalGuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("UUID values must be lowercase canonical strings.");
        }

        var text = reader.GetString();
        if (text is null ||
            !Guid.TryParseExact(text, "D", out var value) ||
            !StringComparer.Ordinal.Equals(text, value.ToString("D")))
        {
            throw new JsonException("UUID values must be lowercase canonical strings.");
        }
        return value;
    }

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D"));
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    GenerationMode = JsonSourceGenerationMode.Default,
    WriteIndented = false)]
[JsonSerializable(typeof(WireEnvelope))]
[JsonSerializable(typeof(HelloPayload))]
[JsonSerializable(typeof(WelcomePayload))]
[JsonSerializable(typeof(FlightStatePayload))]
[JsonSerializable(typeof(CatalogPayload))]
[JsonSerializable(typeof(LeaseAcquirePayload))]
[JsonSerializable(typeof(LeaseGrantedPayload))]
[JsonSerializable(typeof(LeaseReleasePayload))]
[JsonSerializable(typeof(ControlFramePayload))]
[JsonSerializable(typeof(RequestPayload))]
[JsonSerializable(typeof(ResponsePayload))]
[JsonSerializable(typeof(EventPayload))]
[JsonSerializable(typeof(HeartbeatPayload))]
[JsonSerializable(typeof(ErrorPayload))]
internal partial class KittenIoJsonContext : JsonSerializerContext;
