using KittenIO.Protocol;

namespace KittenIO.Mod.Control;

/// <summary>
/// Immutable complete control snapshot passed from networking to the KSA patch.
/// </summary>
internal sealed record NormalizedControlFrame
{
    public required Guid LeaseId { get; init; }
    public required long ControlSequence { get; init; }
    public required string Mode { get; init; }
    public required bool? EngineOn { get; init; }
    public required double? Throttle { get; init; }
    public ThrustVectorTarget? ThrustVectorTarget { get; init; }

    public static NormalizedControlFrame FromPayload(ControlFramePayload value) => new()
    {
        LeaseId = value.LeaseId,
        ControlSequence = value.ControlSequence,
        Mode = value.Mode,
        EngineOn = value.EngineOn,
        Throttle = value.Throttle,
        ThrustVectorTarget = value.ThrustVectorTarget,
    };

    public ControlCommandTelemetry ToTelemetry(long monotonicMs) => new()
    {
        ControlSequence = ControlSequence,
        Mode = Mode,
        EngineOn = EngineOn,
        Throttle = Throttle,
        ThrustVectorTarget = ThrustVectorTarget,
        MonotonicMs = monotonicMs,
    };
}
