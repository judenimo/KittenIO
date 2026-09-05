using KittenIO.Mod.Diagnostics;
using KittenIO.Mod.Game;
using KittenIO.Mod.Runtime;
using KittenIO.Protocol;

namespace KittenIO.Mod.Control;

/// <summary>
/// Canonical facade over the bounded multi-route registry. It admits only one
/// lease, for the currently observed locally controlled craft.
/// </summary>
internal sealed class ControlCoordinator : IVehicleSwitchLeaseBarrier
{
    private readonly object _gate = new();
    private readonly VehicleLeaseRegistry _leases;
    private readonly BridgeDiagnostics _diagnostics;
    private readonly Queue<LeaseReleaseNotice> _releaseNotices = [];
    private readonly GameInstanceId _gameInstance;
    private ActiveVehicleObservation _activeVehicle = new(null, null, false, null, null, null, new AuthorityEpoch(0));
    private Guid? _activeLeaseId;
    private NormalizedControlFrame? _currentControl;
    private PublishedControlFrame? _publishedOverride;
    private AppliedObservation? _pendingApplied;
    private ControlCommandTelemetry? _latestRequested;
    private ControlCommandTelemetry? _accepted;
    private ControlCommandTelemetry? _applied;
    private string? _lastRejectionReason;
    private bool _controlAvailable;
    private bool _vehicleSwitchPending;
    private int _patchFaultPending;

    public ControlCoordinator(
        int timeoutMs,
        BridgeDiagnostics diagnostics,
        GameInstanceId? gameInstance = null,
        VehicleLeaseRegistry? leases = null)
    {
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        TimeoutMs = timeoutMs;
        _diagnostics = diagnostics;
        _gameInstance = gameInstance ?? GameInstanceId.New();
        _leases = leases ?? new VehicleLeaseRegistry();
    }

    public int TimeoutMs { get; }

    public GameInstanceId GameInstance => _gameInstance;

    public bool ControlAvailable
    {
        get
        {
            lock (_gate) return _controlAvailable;
        }
    }

    public void SetControlAvailability(bool available)
    {
        lock (_gate)
        {
            _controlAvailable = available;
            if (!available) ReleaseUnsafe("control unavailable", notifyPeer: true);
        }
    }

    public void UpdateActiveVehicle(ActiveVehicleObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);
        lock (_gate)
        {
            var previous = _activeVehicle;
            _activeVehicle = observation;
            if (_activeLeaseId is not { } leaseId || !_leases.TryGet(leaseId, out var lease) || lease is null)
            {
                return;
            }

            if (RouteMatchesActive(lease.Route, observation)) return;

            var reason = !Nullable.Equals(previous.VehicleId, observation.VehicleId)
                ? "vehicle changed"
                : previous.AuthorityEpoch != observation.AuthorityEpoch
                    ? "vehicle authority changed"
                    : "vehicle is no longer controllable";
            ReleaseUnsafe(reason, notifyPeer: true);
        }
    }

    public bool TryAcquire(
        CommandContext context,
        string vehicleId,
        long nowMs,
        out LeaseGrantedPayload? granted,
        out ProtocolError? error)
    {
        lock (_gate)
        {
            granted = null;
            error = null;
            if (!_controlAvailable)
            {
                error = new("control_unavailable", "Control injection is not compatible with this KSA build.");
                return false;
            }

            if (_activeLeaseId is not null)
            {
                error = new("lease_unavailable", "A control lease is already active; release it before acquiring another.");
                return false;
            }

            if (_vehicleSwitchPending)
            {
                error = new("busy", "An active-vehicle switch is pending; wait for confirmation before acquiring control.");
                return false;
            }

            if (!TryCreateRoute(context, vehicleId, out var route, out error)) return false;
            if (!_leases.TryAcquire(route, nowMs, TimeoutMs, out var lease, out error) || lease is null) return false;

            _activeLeaseId = lease.LeaseId;
            _currentControl = null;
            _latestRequested = null;
            _accepted = null;
            _applied = null;
            _lastRejectionReason = null;
            Volatile.Write(ref _pendingApplied, null);
            Volatile.Write(ref _publishedOverride, null);
            granted = new LeaseGrantedPayload
            {
                LeaseId = lease.LeaseId,
                VehicleId = route.Vehicle.Value,
                TimeoutMs = TimeoutMs,
            };
            _diagnostics.SetLease($"active {lease.LeaseId:N}"[..15]);
            return true;
        }
    }

    public bool TryAccept(
        CommandContext context,
        ControlFramePayload control,
        long nowMs,
        out ProtocolError? error)
    {
        if (!WireCodec.TryValidateControl(control, out error)) return false;
        return TryAccept(context, NormalizedControlFrame.FromPayload(control), nowMs, out error);
    }

    public bool TryAccept(
        CommandContext context,
        NormalizedControlFrame control,
        long nowMs,
        out ProtocolError? error)
    {
        lock (_gate)
        {
            if (_activeLeaseId is not { } leaseId ||
                !_leases.TryGet(leaseId, out var currentLease) ||
                currentLease is null)
            {
                error = new("invalid_lease", "The control lease is missing or stale.");
                return false;
            }

            if (!ContextMatchesRoute(context, currentLease.Route) ||
                !RouteMatchesActive(currentLease.Route, _activeVehicle))
            {
                error = new("invalid_lease", "The control context no longer matches the active authority route.");
                return false;
            }

            if (!_leases.TryAccept(currentLease.Route, control, nowMs, out var accepted, out error) || accepted is null)
            {
                if (error?.Code == "expired_lease")
                {
                    ReleasePublishedState();
                    _activeLeaseId = null;
                    _releaseNotices.Enqueue(CreateReleaseNotice(currentLease, "control dead-man expired"));
                    _diagnostics.SetLease("expired");
                }
                return false;
            }

            _currentControl = control;
            _accepted = control.ToTelemetry(nowMs);
            _lastRejectionReason = null;
            Volatile.Write(ref _publishedOverride, new PublishedControlFrame(
                accepted.Route,
                control,
                accepted.ValidThroughMonotonicMs));
            return true;
        }
    }

    /// <summary>
    /// Read-only receive-thread admission check. The game thread remains the
    /// sole owner of sequence advancement, deadlines, and override publication.
    /// </summary>
    public bool TryValidateInbound(
        CommandContext context,
        ControlFramePayload control,
        long nowMs,
        out ProtocolError? error)
    {
        if (!WireCodec.TryValidateControl(control, out error)) return false;
        return TryValidateInbound(context, NormalizedControlFrame.FromPayload(control), nowMs, out error);
    }

    public bool TryValidateInbound(
        CommandContext context,
        NormalizedControlFrame control,
        long nowMs,
        out ProtocolError? error)
    {
        lock (_gate)
        {
            _latestRequested = control.ToTelemetry(nowMs);
            if (_activeLeaseId is not { } leaseId ||
                control.LeaseId != leaseId ||
                !_leases.TryGet(leaseId, out var lease) ||
                lease is null ||
                !ContextMatchesRoute(context, lease.Route) ||
                !RouteMatchesActive(lease.Route, _activeVehicle))
            {
                error = new("invalid_lease", "The control frame does not belong to the active authority route.");
                _lastRejectionReason = error.Value.Code;
                return false;
            }

            if (nowMs >= lease.ValidThroughMonotonicMs)
            {
                error = new("expired_lease", "The control lease expired before this frame arrived.");
                _lastRejectionReason = error.Value.Code;
                return false;
            }

            if (control.ControlSequence <= lease.LastControlSequence)
            {
                error = new("stale_control", "control_seq must increase within a lease.");
                _lastRejectionReason = error.Value.Code;
                return false;
            }

            error = null;
            return true;
        }
    }

    public bool Release(CommandContext context, Guid leaseId, string reason)
    {
        lock (_gate)
        {
            if (_activeLeaseId != leaseId ||
                !_leases.TryGet(leaseId, out var lease) ||
                lease is null ||
                !ContextMatchesRoute(context, lease.Route))
            {
                return false;
            }

            return ReleaseUnsafe(leaseId, reason, notifyPeer: false);
        }
    }

    public bool Release(Guid? leaseId, string reason)
    {
        lock (_gate)
        {
            if (_activeLeaseId is not { } active || leaseId is { } expected && active != expected) return false;
            return ReleaseUnsafe(active, reason, notifyPeer: false);
        }
    }

    public void ReleaseAll(string reason, bool notifyPeer = true)
    {
        lock (_gate) ReleaseUnsafe(reason, notifyPeer);
    }

    public int ReleaseSession(ProtocolSessionId session, string reason, bool notifyPeer = false)
    {
        lock (_gate) return ApplyScopedRelease(_leases.ReleaseSession(session), reason, notifyPeer);
    }

    public int ReleaseController(EphemeralControllerId controller, string reason, bool notifyPeer = false)
    {
        lock (_gate) return ApplyScopedRelease(_leases.ReleaseController(controller), reason, notifyPeer);
    }

    public int ReleaseVehicle(VehicleId vehicle, string reason, bool notifyPeer = true)
    {
        lock (_gate) return ApplyScopedRelease(_leases.ReleaseVehicle(vehicle), reason, notifyPeer);
    }

    public int ReleaseEpoch(AuthorityEpoch epoch, string reason, bool notifyPeer = true)
    {
        lock (_gate) return ApplyScopedRelease(_leases.ReleaseEpoch(epoch), reason, notifyPeer);
    }

    public bool ExpireIfStale(long nowMs)
    {
        lock (_gate)
        {
            var expired = _leases.Expire(nowMs);
            return ApplyScopedRelease(expired, "control dead-man expired", notifyPeer: true) > 0;
        }
    }

    public bool IsLeaseActive
    {
        get
        {
            lock (_gate) return _activeLeaseId is not null;
        }
    }

    public bool VehicleSwitchPending
    {
        get
        {
            lock (_gate) return _vehicleSwitchPending;
        }
    }

    public bool TryEnterVehicleSwitch(out ProtocolError? error)
    {
        lock (_gate)
        {
            if (_activeLeaseId is not null)
            {
                error = new("lease_conflict", "Release the active control lease before switching vehicles.");
                return false;
            }
            if (_vehicleSwitchPending)
            {
                error = new("busy", "Another active-vehicle switch is already pending.");
                return false;
            }

            _vehicleSwitchPending = true;
            ReleasePublishedState();
            error = null;
            return true;
        }
    }

    public void ExitVehicleSwitch()
    {
        lock (_gate) _vehicleSwitchPending = false;
    }

    public bool TryReadReleaseNotice(out LeaseReleaseNotice? notice)
    {
        lock (_gate)
        {
            if (_releaseNotices.Count == 0)
            {
                notice = null;
                return false;
            }

            notice = _releaseNotices.Dequeue();
            return true;
        }
    }

    public void ClearReleaseNotices()
    {
        lock (_gate) _releaseNotices.Clear();
    }

    public bool IsAuthorized(CommandContext context, string vehicleId, Guid leaseId)
    {
        lock (_gate)
        {
            return _activeLeaseId == leaseId &&
                   _leases.TryGet(leaseId, out var lease) &&
                   lease is not null &&
                   StringComparer.Ordinal.Equals(lease.Route.Vehicle.Value, vehicleId) &&
                   ContextMatchesRoute(context, lease.Route) &&
                   RouteMatchesActive(lease.Route, _activeVehicle);
        }
    }

    public bool TryBindAuthorizedRoute(
        CommandContext context,
        string vehicleId,
        Guid leaseId,
        out CommandContext boundContext)
    {
        lock (_gate)
        {
            if (_activeLeaseId == leaseId &&
                _leases.TryGet(leaseId, out var lease) &&
                lease is not null &&
                StringComparer.Ordinal.Equals(lease.Route.Vehicle.Value, vehicleId) &&
                ContextMatchesRoute(context, lease.Route) &&
                RouteMatchesActive(lease.Route, _activeVehicle))
            {
                boundContext = context.WithRoute(lease.Route, leaseId);
                return true;
            }

            boundContext = context;
            return false;
        }
    }

    public AppliedControlTelemetry CaptureAppliedControl()
    {
        lock (_gate)
        {
            var observed = Interlocked.Exchange(ref _pendingApplied, null);
            if (observed is not null)
            {
                _applied = observed.Control.ToTelemetry(observed.MonotonicMs);
            }

            return new AppliedControlTelemetry
            {
                LeaseActive = _activeLeaseId is not null,
                LeaseId = _activeLeaseId,
                ActiveMode = _currentControl?.Mode,
                LatestRequested = _latestRequested,
                Accepted = _accepted,
                Applied = _applied,
                // KSA applies the copied input and flight-computer target on a
                // worker path which exposes no authoritative game frame or
                // simulation clock. Keep these explicitly unavailable rather
                // than mislabelling the later telemetry-capture timestamp.
                AppliedGameFrame = null,
                AppliedSimulationTimeS = null,
                RejectionReason = _lastRejectionReason,
                SaturationReason = null,
            };
        }
    }

    public bool TryGetFreshOverrideForControlledVehicle(
        long nowMs,
        VehicleInstanceIdentity preparedVehicle,
        out NormalizedControlFrame? control)
    {
        var published = Volatile.Read(ref _publishedOverride);
        if (published is null ||
            !published.Route.IsVerifiedLocalRoute ||
            !ReferenceEquals(published.Route.VehicleInstance, preparedVehicle) ||
            nowMs >= published.ValidThroughMonotonicMs)
        {
            control = null;
            return false;
        }

        control = published.Control;
        return true;
    }

    /// <summary>Called after a verified KSA input/target mutation; never takes the coordinator lock.</summary>
    public void ReportApplied(NormalizedControlFrame control, long nowMs) =>
        Volatile.Write(ref _pendingApplied, new AppliedObservation(control, nowMs));

    /// <summary>Called by Harmony without locks; the main thread performs cleanup.</summary>
    public void ReportPatchFault() => Interlocked.Exchange(ref _patchFaultPending, 1);

    public bool ReleaseForPendingPatchFault()
    {
        if (Interlocked.Exchange(ref _patchFaultPending, 0) == 0) return false;
        lock (_gate)
        {
            ReleaseUnsafe("control patch fault", notifyPeer: true);
            return true;
        }
    }

    private bool TryCreateRoute(
        CommandContext context,
        string requestedVehicleId,
        out ControlRoute route,
        out ProtocolError? error)
    {
        route = default;
        error = null;
        if (context.GameInstance != _gameInstance)
        {
            error = new("wrong_game_instance", "The command context belongs to another loaded game instance.");
            return false;
        }

        if (_activeVehicle is not { HasVerifiedLocalAuthority: true, VehicleId: { } vehicle, Authority: { } authority })
        {
            error = new("no_active_vehicle", "No locally controlled, controllable vehicle is active.");
            return false;
        }

        if (!StringComparer.Ordinal.Equals(requestedVehicleId, vehicle.Value))
        {
            error = new("vehicle_changed", "The requested vehicle is no longer the active controlled vehicle.");
            return false;
        }

        route = new ControlRoute(
            context.GameInstance,
            context.Controller,
            context.Session,
            vehicle,
            _activeVehicle.VehicleInstance!,
            authority,
            _activeVehicle.AuthorityEpoch,
            ControlMode.ActiveVehicleLease);
        return true;
    }

    private static bool ContextMatchesRoute(CommandContext context, ControlRoute route) =>
        context.GameInstance == route.GameInstance &&
        context.Controller == route.Controller &&
        context.Session == route.Session;

    private static bool RouteMatchesActive(ControlRoute route, ActiveVehicleObservation observation) =>
        observation.HasVerifiedLocalAuthority &&
        observation.VehicleId == route.Vehicle &&
        ReferenceEquals(observation.VehicleInstance, route.VehicleInstance) &&
        observation.Authority == route.Authority &&
        observation.AuthorityEpoch == route.Epoch;

    private bool ReleaseUnsafe(string reason, bool notifyPeer)
    {
        var released = _leases.ReleaseAll();
        if (released.Count == 0)
        {
            _activeLeaseId = null;
            ReleasePublishedState();
            return false;
        }

        foreach (var lease in released)
        {
            if (notifyPeer)
            {
                _releaseNotices.Enqueue(CreateReleaseNotice(lease, reason));
            }
        }
        _activeLeaseId = null;
        ReleasePublishedState();
        _diagnostics.SetLease($"released: {reason}");
        return true;
    }

    private bool ReleaseUnsafe(Guid leaseId, string reason, bool notifyPeer)
    {
        if (!_leases.ReleaseLease(leaseId, out var released) || released is null) return false;
        if (notifyPeer)
        {
            _releaseNotices.Enqueue(CreateReleaseNotice(released, reason));
        }
        if (_activeLeaseId == leaseId) _activeLeaseId = null;
        ReleasePublishedState();
        _diagnostics.SetLease($"released: {reason}");
        return true;
    }

    private int ApplyScopedRelease(IReadOnlyList<VehicleLeaseSnapshot> released, string reason, bool notifyPeer)
    {
        if (released.Count == 0) return 0;
        var activeReleased = false;
        foreach (var lease in released)
        {
            if (notifyPeer)
            {
                _releaseNotices.Enqueue(CreateReleaseNotice(lease, reason));
            }
            if (_activeLeaseId == lease.LeaseId)
            {
                _activeLeaseId = null;
                activeReleased = true;
            }
        }
        if (activeReleased)
        {
            ReleasePublishedState();
            _diagnostics.SetLease($"released: {reason}");
        }
        return released.Count;
    }

    private static LeaseReleaseNotice CreateReleaseNotice(VehicleLeaseSnapshot lease, string reason) => new(
        new CommandContext(
            lease.Route.GameInstance,
            lease.Route.Controller,
            lease.Route.Session,
            ProtocolConstants.MessageTypes.LeaseRelease,
            0,
            0,
            null,
            lease.Route,
            lease.LeaseId),
        new LeaseReleasePayload { LeaseId = lease.LeaseId, Reason = reason });

    private void ReleasePublishedState()
    {
        _currentControl = null;
        Volatile.Write(ref _publishedOverride, null);
        Volatile.Write(ref _pendingApplied, null);
    }

    private sealed record PublishedControlFrame(
        ControlRoute Route,
        NormalizedControlFrame Control,
        long ValidThroughMonotonicMs);

    private sealed record AppliedObservation(NormalizedControlFrame Control, long MonotonicMs);
}

internal sealed record LeaseReleaseNotice(
    CommandContext Context,
    LeaseReleasePayload Payload);
