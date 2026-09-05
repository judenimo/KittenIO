using KittenIO.Protocol;

namespace KittenIO.Mod.Networking;

/// <summary>
/// Capacity-one latest control slot that never replaces a newer pending frame
/// with a non-increasing frame for the same exact session and lease.
/// </summary>
internal sealed class LatestInboundControlSlot
{
    private readonly object _gate = new();
    private InboundControl? _pending;

    public bool TryWrite(InboundControl inbound, out ProtocolError? error)
    {
        ArgumentNullException.ThrowIfNull(inbound);
        lock (_gate)
        {
            if (_pending is { } pending &&
                pending.Context.GameInstance == inbound.Context.GameInstance &&
                pending.Context.Controller == inbound.Context.Controller &&
                pending.Context.Session == inbound.Context.Session &&
                pending.Payload.LeaseId == inbound.Payload.LeaseId &&
                inbound.Payload.ControlSequence <= pending.Payload.ControlSequence)
            {
                error = new(
                    "stale_control",
                    "control_seq must increase without replacing a newer pending frame.");
                return false;
            }

            _pending = inbound;
            error = null;
            return true;
        }
    }

    public bool TryRead(out InboundControl? inbound)
    {
        lock (_gate)
        {
            inbound = _pending;
            _pending = null;
            return inbound is not null;
        }
    }

    public void Clear()
    {
        lock (_gate) _pending = null;
    }
}
