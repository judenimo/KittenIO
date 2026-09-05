using KittenIO.Mod.Control;
using KittenIO.Mod.Runtime;

namespace KittenIO.Mod.Game;

internal interface IActiveVehicleSwitchAdapter
{
    bool ActiveVehicleSwitchContractVerified { get; }

    string ActiveVehicleSwitchContractFailureReason { get; }

    ActiveVehicleObservation ActiveVehicle { get; }

    GameCommandResult ValidateActiveVehicleSwitchTarget(string vehicleId);

    VehicleSwitchRequestResult RequestActiveVehicleSwitch(string vehicleId);
}

/// <summary>
/// Game-thread state machine for one v1 active-vehicle transition. Enqueueing
/// KSA's input event is not treated as success: two distinct later frame
/// observations must verify the same exact vehicle instance and authority route.
/// </summary>
internal sealed class ActiveVehicleSwitchCoordinator
{
    public const long DefaultTimeoutMs = 5_000;
    private readonly IActiveVehicleSwitchAdapter _adapter;
    private readonly IVehicleSwitchLeaseBarrier _leaseBarrier;
    private readonly GameInstanceId _gameInstance;
    private readonly long _timeoutMs;
    private PendingSwitch? _pending;

    public ActiveVehicleSwitchCoordinator(
        IActiveVehicleSwitchAdapter adapter,
        IVehicleSwitchLeaseBarrier leaseBarrier,
        GameInstanceId gameInstance,
        long timeoutMs = DefaultTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(adapter);
        ArgumentNullException.ThrowIfNull(leaseBarrier);
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        _adapter = adapter;
        _leaseBarrier = leaseBarrier;
        _gameInstance = gameInstance;
        _timeoutMs = timeoutMs;
    }

    public bool IsPending => _pending is not null;

    public bool TryBegin(
        CommandContext context,
        Guid commandId,
        string vehicleId,
        long frameOrdinal,
        long nowMs,
        out GameCommandResult? failure)
    {
        failure = null;
        if (context.GameInstance != _gameInstance)
        {
            failure = GameCommandResult.Failure(
                "invalid_params",
                "The switch request belongs to another loaded game instance.");
            return false;
        }
        if (commandId == Guid.Empty || string.IsNullOrWhiteSpace(vehicleId))
        {
            failure = GameCommandResult.Failure(
                "invalid_params",
                "set_active_vehicle requires a canonical non-empty command_id and vehicle_id.");
            return false;
        }
        if (_pending is not null)
        {
            failure = GameCommandResult.Failure("busy", "Another active-vehicle switch is already pending.");
            return false;
        }
        if (!_leaseBarrier.TryEnterVehicleSwitch(out var barrierError))
        {
            failure = GameCommandResult.Failure(
                barrierError?.Code ?? "busy",
                barrierError?.Message ?? "Active-vehicle switching is temporarily unavailable.");
            return false;
        }

        try
        {
            var request = _adapter.RequestActiveVehicleSwitch(vehicleId);
            if (!request.Ok ||
                request.Vehicle is null ||
                request.VehicleInstance is null ||
                request.FlightComputerInstance is null)
            {
                _leaseBarrier.ExitVehicleSwitch();
                failure = GameCommandResult.Failure(request.Code, request.Message);
                return false;
            }

            var initial = _adapter.ActiveVehicle;
            _pending = new PendingSwitch(
                context,
                commandId,
                request.Vehicle.Value,
                request.AlreadyActive,
                request.VehicleInstance,
                request.FlightComputerInstance,
                initial.AuthorityEpoch,
                frameOrdinal,
                checked(nowMs + _timeoutMs));
            return true;
        }
        catch (Exception)
        {
            _leaseBarrier.ExitVehicleSwitch();
            failure = GameCommandResult.Failure(
                "switch_failed",
                "The active-vehicle switch could not be admitted safely.");
            return false;
        }
    }

    public bool TryAdvance(
        long frameOrdinal,
        long nowMs,
        out VehicleSwitchCompletion? completion)
    {
        completion = null;
        var pending = _pending;
        if (pending is null) return false;
        if (nowMs >= pending.DeadlineMonotonicMs)
        {
            completion = Finish(
                pending,
                GameCommandResult.Failure(
                    "switch_failed",
                    "KSA did not confirm the requested active vehicle within five seconds."));
            return true;
        }
        if (frameOrdinal <= pending.BeginFrameOrdinal || frameOrdinal <= pending.LastObservedFrameOrdinal)
        {
            return false;
        }
        pending.LastObservedFrameOrdinal = frameOrdinal;

        GameCommandResult targetStatus;
        try
        {
            targetStatus = _adapter.ValidateActiveVehicleSwitchTarget(pending.Vehicle.Value);
        }
        catch (Exception)
        {
            completion = Finish(
                pending,
                GameCommandResult.Failure(
                    "switch_failed",
                    "KSA's active-vehicle switch state became unavailable."));
            return true;
        }
        if (!targetStatus.Ok)
        {
            completion = Finish(pending, targetStatus);
            return true;
        }

        var observation = _adapter.ActiveVehicle;
        var epochConfirmed = pending.AlreadyActive || observation.AuthorityEpoch.Value > pending.InitialAuthorityEpoch.Value;
        var routeConfirmed = epochConfirmed &&
            observation.HasVerifiedLocalAuthority &&
            observation.VehicleId == pending.Vehicle &&
            ReferenceEquals(observation.VehicleInstance, pending.VehicleInstance) &&
            ReferenceEquals(observation.FlightComputerInstance, pending.FlightComputerInstance);
        if (!routeConfirmed)
        {
            pending.ResetConfirmation();
            return false;
        }

        if (!ReferenceEquals(pending.ConfirmedVehicleInstance, observation.VehicleInstance) ||
            pending.ConfirmedAuthorityEpoch != observation.AuthorityEpoch)
        {
            pending.ConfirmedVehicleInstance = observation.VehicleInstance;
            pending.ConfirmedAuthorityEpoch = observation.AuthorityEpoch;
            pending.ConsecutiveConfirmations = 1;
        }
        else
        {
            pending.ConsecutiveConfirmations++;
        }

        if (pending.ConsecutiveConfirmations < 2) return false;
        completion = Finish(
            pending,
            GameCommandResult.Success(new { vehicle_id = pending.Vehicle.Value }));
        return true;
    }

    public bool CancelSession(ProtocolSessionId session) =>
        CancelWhere(pending => pending.Context.Session == session);

    public bool CancelController(EphemeralControllerId controller) =>
        CancelWhere(pending => pending.Context.Controller == controller);

    public bool CancelAll() => CancelWhere(_ => true);

    private VehicleSwitchCompletion Finish(PendingSwitch pending, GameCommandResult result)
    {
        _pending = null;
        _leaseBarrier.ExitVehicleSwitch();
        return new VehicleSwitchCompletion(
            pending.Context,
            pending.CommandId,
            pending.Vehicle,
            result);
    }

    private bool CancelWhere(Func<PendingSwitch, bool> predicate)
    {
        var pending = _pending;
        if (pending is null || !predicate(pending)) return false;
        _pending = null;
        _leaseBarrier.ExitVehicleSwitch();
        return true;
    }

    private sealed class PendingSwitch(
        CommandContext context,
        Guid commandId,
        VehicleId vehicle,
        bool alreadyActive,
        VehicleInstanceIdentity vehicleInstance,
        FlightComputerInstanceIdentity flightComputerInstance,
        AuthorityEpoch initialAuthorityEpoch,
        long beginFrameOrdinal,
        long deadlineMonotonicMs)
    {
        public CommandContext Context { get; } = context;
        public Guid CommandId { get; } = commandId;
        public VehicleId Vehicle { get; } = vehicle;
        public bool AlreadyActive { get; } = alreadyActive;
        public VehicleInstanceIdentity VehicleInstance { get; } = vehicleInstance;
        public FlightComputerInstanceIdentity FlightComputerInstance { get; } = flightComputerInstance;
        public AuthorityEpoch InitialAuthorityEpoch { get; } = initialAuthorityEpoch;
        public long BeginFrameOrdinal { get; } = beginFrameOrdinal;
        public long DeadlineMonotonicMs { get; } = deadlineMonotonicMs;
        public long LastObservedFrameOrdinal { get; set; } = beginFrameOrdinal;
        public VehicleInstanceIdentity? ConfirmedVehicleInstance { get; set; }
        public AuthorityEpoch? ConfirmedAuthorityEpoch { get; set; }
        public int ConsecutiveConfirmations { get; set; }

        public void ResetConfirmation()
        {
            ConfirmedVehicleInstance = null;
            ConfirmedAuthorityEpoch = null;
            ConsecutiveConfirmations = 0;
        }
    }
}

internal sealed record VehicleSwitchRequestResult(
    bool Ok,
    string Code,
    string Message,
    VehicleId? Vehicle,
    bool AlreadyActive,
    VehicleInstanceIdentity? VehicleInstance,
    FlightComputerInstanceIdentity? FlightComputerInstance)
{
    public static VehicleSwitchRequestResult Success(
        VehicleId vehicle,
        bool alreadyActive,
        VehicleInstanceIdentity vehicleInstance,
        FlightComputerInstanceIdentity flightComputerInstance) =>
        new(
            true,
            "ok",
            "KSA accepted the active-vehicle switch request.",
            vehicle,
            alreadyActive,
            vehicleInstance,
            flightComputerInstance);

    public static VehicleSwitchRequestResult Failure(string code, string message) =>
        new(false, code, message, null, false, null, null);
}

internal sealed record VehicleSwitchCompletion(
    CommandContext Context,
    Guid CommandId,
    VehicleId Vehicle,
    GameCommandResult Result);
