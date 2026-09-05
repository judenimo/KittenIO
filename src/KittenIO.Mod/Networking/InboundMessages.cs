using KittenIO.Mod.Runtime;
using KittenIO.Mod.Control;
using KittenIO.Protocol;

namespace KittenIO.Mod.Networking;

internal sealed record InboundControl(
    CommandContext Context,
    NormalizedControlFrame Payload,
    long ReceivedMonotonicMs)
{
    public InboundControl(CommandContext context, ControlFramePayload payload, long receivedMonotonicMs)
        : this(context, NormalizedControlFrame.FromPayload(payload), receivedMonotonicMs)
    {
    }

    public Guid SessionId => Context.Session.Value;

    public string? RequestId => Context.RequestId;
}

internal abstract record MainThreadMessage(CommandContext Context)
{
    public Guid SessionId => Context.Session.Value;

    public string? RequestId => Context.RequestId;
}

internal sealed record AcquireLeaseMessage(
    CommandContext Context,
    LeaseAcquirePayload Payload)
    : MainThreadMessage(Context);

internal sealed record ReleaseLeaseMessage(
    CommandContext Context,
    LeaseReleasePayload Payload)
    : MainThreadMessage(Context);

internal sealed record GameRequestMessage(
    CommandContext Context,
    RequestPayload Payload)
    : MainThreadMessage(Context);
