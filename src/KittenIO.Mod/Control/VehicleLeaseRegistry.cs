using KittenIO.Mod.Runtime;
using KittenIO.Protocol;

namespace KittenIO.Mod.Control;

/// <summary>
/// Bounded lease storage whose routes, sequence spaces, freshness deadlines,
/// authority epochs, and overrides are independent. Protocol v1 deliberately
/// exposes only one active route through <see cref="ControlCoordinator"/>.
/// </summary>
internal sealed class VehicleLeaseRegistry(int capacity = 64)
{
    public const int DefaultCapacity = 64;

    private readonly object _gate = new();
    private readonly Dictionary<Guid, VehicleLeaseSnapshot> _leases = [];

    public int Capacity { get; } = capacity is > 0 and <= DefaultCapacity
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));

    public int Count
    {
        get
        {
            lock (_gate) return _leases.Count;
        }
    }

    public bool TryAcquire(
        ControlRoute route,
        long nowMonotonicMs,
        int timeoutMs,
        out VehicleLeaseSnapshot? lease,
        out ProtocolError? error)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowMonotonicMs);
        if (timeoutMs <= 0) throw new ArgumentOutOfRangeException(nameof(timeoutMs));

        lock (_gate)
        {
            lease = null;
            error = null;
            if (!route.IsVerifiedLocalRoute)
            {
                error = new("invalid_authority", "Control requires verified local KSA input authority.");
                return false;
            }

            if (_leases.Count >= Capacity)
            {
                error = new("lease_capacity", "The bounded vehicle lease registry is full.");
                return false;
            }

            if (_leases.Values.Any(item => item.Route == route))
            {
                error = new("lease_unavailable", "This control route already has an active lease.");
                return false;
            }

            var leaseId = Guid.NewGuid();
            lease = new VehicleLeaseSnapshot(
                leaseId,
                route,
                -1,
                Deadline(nowMonotonicMs, timeoutMs),
                timeoutMs,
                null);
            _leases.Add(leaseId, lease);
            return true;
        }
    }

    public bool TryAccept(
        ControlRoute route,
        ControlFramePayload control,
        long nowMonotonicMs,
        out VehicleLeaseSnapshot? accepted,
        out ProtocolError? error)
    {
        if (!WireCodec.TryValidateControl(control, out error))
        {
            accepted = null;
            return false;
        }
        return TryAccept(route, NormalizedControlFrame.FromPayload(control), nowMonotonicMs, out accepted, out error);
    }

    public bool TryAccept(
        ControlRoute route,
        NormalizedControlFrame control,
        long nowMonotonicMs,
        out VehicleLeaseSnapshot? accepted,
        out ProtocolError? error)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowMonotonicMs);
        accepted = null;
        error = null;

        lock (_gate)
        {
            if (!_leases.TryGetValue(control.LeaseId, out var current) || current.Route != route)
            {
                error = new("invalid_lease", "The control lease is missing, stale, or belongs to another route.");
                return false;
            }

            if (nowMonotonicMs >= current.ValidThroughMonotonicMs)
            {
                _leases.Remove(control.LeaseId);
                error = new("expired_lease", "The control lease expired before this frame arrived.");
                return false;
            }

            if (control.ControlSequence <= current.LastControlSequence)
            {
                error = new("stale_control", "control_seq must increase within a lease.");
                return false;
            }

            accepted = current with
            {
                LastControlSequence = control.ControlSequence,
                ValidThroughMonotonicMs = Deadline(nowMonotonicMs, current.TimeoutMs),
                Override = control,
            };
            _leases[control.LeaseId] = accepted;
            error = null;
            return true;
        }
    }

    public bool TryGet(Guid leaseId, out VehicleLeaseSnapshot? lease)
    {
        lock (_gate) return _leases.TryGetValue(leaseId, out lease);
    }

    public bool TryGetFreshOverride(
        Guid leaseId,
        ControlRoute route,
        long nowMonotonicMs,
        out NormalizedControlFrame? control)
    {
        lock (_gate)
        {
            if (_leases.TryGetValue(leaseId, out var lease) &&
                lease.Route == route &&
                lease.Override is { } fresh &&
                nowMonotonicMs < lease.ValidThroughMonotonicMs)
            {
                control = fresh;
                return true;
            }

            control = null;
            return false;
        }
    }

    public bool ReleaseLease(Guid leaseId, out VehicleLeaseSnapshot? released)
    {
        lock (_gate) return _leases.Remove(leaseId, out released);
    }

    public IReadOnlyList<VehicleLeaseSnapshot> ReleaseVehicle(VehicleId vehicle) =>
        ReleaseWhere(item => item.Route.Vehicle == vehicle);

    public IReadOnlyList<VehicleLeaseSnapshot> ReleaseSession(ProtocolSessionId session) =>
        ReleaseWhere(item => item.Route.Session == session);

    public IReadOnlyList<VehicleLeaseSnapshot> ReleaseController(EphemeralControllerId controller) =>
        ReleaseWhere(item => item.Route.Controller == controller);

    public IReadOnlyList<VehicleLeaseSnapshot> ReleaseEpoch(AuthorityEpoch epoch) =>
        ReleaseWhere(item => item.Route.Epoch == epoch);

    public IReadOnlyList<VehicleLeaseSnapshot> ReleaseAll() => ReleaseWhere(static _ => true);

    public IReadOnlyList<VehicleLeaseSnapshot> Expire(long nowMonotonicMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowMonotonicMs);
        return ReleaseWhere(item => nowMonotonicMs >= item.ValidThroughMonotonicMs);
    }

    private IReadOnlyList<VehicleLeaseSnapshot> ReleaseWhere(Func<VehicleLeaseSnapshot, bool> predicate)
    {
        lock (_gate)
        {
            if (_leases.Count == 0) return [];
            var released = _leases.Values.Where(predicate).ToArray();
            foreach (var lease in released) _leases.Remove(lease.LeaseId);
            return released;
        }
    }

    private static long Deadline(long nowMs, int timeoutMs) =>
        nowMs > long.MaxValue - timeoutMs ? long.MaxValue : nowMs + timeoutMs;
}

internal sealed record VehicleLeaseSnapshot(
    Guid LeaseId,
    ControlRoute Route,
    long LastControlSequence,
    long ValidThroughMonotonicMs,
    int TimeoutMs,
    NormalizedControlFrame? Override);
