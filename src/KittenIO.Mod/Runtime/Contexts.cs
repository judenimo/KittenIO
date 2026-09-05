namespace KittenIO.Mod.Runtime;

internal readonly record struct GameInstanceId
{
    public GameInstanceId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Game instance ID must be non-empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static GameInstanceId New() => new(Guid.NewGuid());
}

internal readonly record struct EphemeralControllerId
{
    public EphemeralControllerId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Controller ID must be non-empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }

    public static EphemeralControllerId New() => new(Guid.NewGuid());
}

internal readonly record struct ProtocolSessionId
{
    public ProtocolSessionId(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Protocol session ID must be non-empty.", nameof(value));
        Value = value;
    }

    public Guid Value { get; }
}

internal readonly record struct ProtocolPeerContext(
    GameInstanceId GameInstance,
    EphemeralControllerId Controller,
    ProtocolSessionId Session);

internal readonly record struct VehicleId
{
    public VehicleId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}

internal readonly record struct AuthorityIdentity
{
    public const string KsaLocalControlledInputValue = "ksa.local-controlled-input";

    public AuthorityIdentity(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        Value = value;
    }

    public string Value { get; }

    public static AuthorityIdentity KsaLocalControlledInput { get; } = new(KsaLocalControlledInputValue);

    public bool IsVerifiedLocal => StringComparer.Ordinal.Equals(Value, KsaLocalControlledInputValue);

    public override string ToString() => Value;
}

internal readonly record struct AuthorityEpoch
{
    public AuthorityEpoch(long value)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        Value = value;
    }

    public long Value { get; }

    public AuthorityEpoch Next() => new(checked(Value + 1));
}

/// <summary>
/// Opaque, reference-identity token for one live KSA vehicle instance. The KSA
/// object itself never leaves the adapter/patch boundary.
/// </summary>
internal sealed class VehicleInstanceIdentity;

/// <summary>
/// Opaque, reference-identity token for one live KSA flight-computer instance.
/// The KSA object itself never leaves the adapter boundary.
/// </summary>
internal sealed class FlightComputerInstanceIdentity;

internal enum ControlMode
{
    TelemetryOnly = 0,
    ActiveVehicleLease = 1,
}

internal readonly record struct ControlRoute(
    GameInstanceId GameInstance,
    EphemeralControllerId Controller,
    ProtocolSessionId Session,
    VehicleId Vehicle,
    VehicleInstanceIdentity VehicleInstance,
    AuthorityIdentity Authority,
    AuthorityEpoch Epoch,
    ControlMode Mode)
{
    public bool IsVerifiedLocalRoute =>
        Mode == ControlMode.ActiveVehicleLease &&
        VehicleInstance is not null &&
        Authority.IsVerifiedLocal;
}

internal readonly record struct TelemetryStream(
    GameInstanceId GameInstance,
    EphemeralControllerId Controller,
    ProtocolSessionId Session,
    string Key)
{
    public const string ActiveFlightKey = "active-flight";

    public static TelemetryStream ActiveFlight(
        GameInstanceId gameInstance,
        EphemeralControllerId controller,
        ProtocolSessionId session) =>
        new(gameInstance, controller, session, ActiveFlightKey);
}

internal readonly record struct CommandContext(
    GameInstanceId GameInstance,
    EphemeralControllerId Controller,
    ProtocolSessionId Session,
    string MessageType,
    long EnvelopeSequence,
    long ReceivedMonotonicMs,
    string? RequestId,
    ControlRoute? Route = null,
    Guid? LeaseId = null)
{
    public int ProtocolVersion { get; init; } = 1;

    public CommandContext WithRoute(ControlRoute route, Guid leaseId) => this with { Route = route, LeaseId = leaseId };
}

internal readonly record struct CommandScope(
    GameInstanceId GameInstance,
    EphemeralControllerId Controller,
    ProtocolSessionId Session,
    VehicleId? Vehicle,
    AuthorityEpoch? Epoch)
{
    public static CommandScope From(CommandContext context) => new(
        context.GameInstance,
        context.Controller,
        context.Session,
        context.Route?.Vehicle,
        context.Route?.Epoch);
}
