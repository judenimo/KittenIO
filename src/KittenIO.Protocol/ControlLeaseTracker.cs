namespace KittenIO.Protocol;

/// <summary>
/// Game-independent lease and control-sequence validation. The caller supplies a monotonic clock.
/// </summary>
public sealed class ControlLeaseTracker
{
    private readonly long _timeoutMs;
    private readonly object _gate = new();
    private LeaseSnapshot? _current;

    public ControlLeaseTracker(long timeoutMs = ProtocolConstants.DefaultControlTimeoutMs)
    {
        if (timeoutMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(timeoutMs));
        }

        _timeoutMs = timeoutMs;
    }

    public LeaseSnapshot? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public LeaseSnapshot Acquire(Guid sessionId, string vehicleId, long nowMonotonicMs)
    {
        if (sessionId == Guid.Empty)
        {
            throw new ArgumentException("Session ID must be non-empty.", nameof(sessionId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(vehicleId);
        ArgumentOutOfRangeException.ThrowIfNegative(nowMonotonicMs);

        lock (_gate)
        {
            _current = new LeaseSnapshot(
                Guid.NewGuid(),
                sessionId,
                vehicleId,
                -1,
                nowMonotonicMs);
            return _current;
        }
    }

    public bool TryAccept(
        Guid sessionId,
        string vehicleId,
        ControlFramePayload control,
        long nowMonotonicMs,
        out ProtocolError? error)
    {
        error = null;
        if (!WireCodec.TryValidateControl(control, out error))
        {
            return false;
        }

        lock (_gate)
        {
            if (_current is not { } lease ||
                lease.LeaseId != control.LeaseId ||
                lease.SessionId != sessionId ||
                !StringComparer.Ordinal.Equals(lease.VehicleId, vehicleId))
            {
                error = new("invalid_lease", "The control lease is missing, stale, or belongs to another vehicle.");
                return false;
            }

            if (nowMonotonicMs - lease.LastControlMonotonicMs >= _timeoutMs)
            {
                _current = null;
                error = new("expired_lease", "The control lease expired before this frame arrived.");
                return false;
            }

            if (control.ControlSequence <= lease.LastControlSequence)
            {
                error = new("stale_control", "control_seq must increase within a lease.");
                return false;
            }

            _current = lease with
            {
                LastControlSequence = control.ControlSequence,
                LastControlMonotonicMs = nowMonotonicMs,
            };
            return true;
        }
    }

    public bool ExpireIfStale(long nowMonotonicMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nowMonotonicMs);
        lock (_gate)
        {
            if (_current is not { } lease || nowMonotonicMs - lease.LastControlMonotonicMs < _timeoutMs)
            {
                return false;
            }

            _current = null;
            return true;
        }
    }

    public bool Release(Guid? leaseId = null)
    {
        lock (_gate)
        {
            if (_current is null || leaseId is { } expected && _current.LeaseId != expected)
            {
                return false;
            }

            _current = null;
            return true;
        }
    }

    public bool ReleaseIfVehicleChanged(string? vehicleId)
    {
        lock (_gate)
        {
            if (_current is not { } lease || StringComparer.Ordinal.Equals(lease.VehicleId, vehicleId))
            {
                return false;
            }

            _current = null;
            return true;
        }
    }
}

public sealed record LeaseSnapshot(
    Guid LeaseId,
    Guid SessionId,
    string VehicleId,
    long LastControlSequence,
    long LastControlMonotonicMs);
