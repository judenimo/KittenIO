using KittenIO.Protocol;

namespace KittenIO.Mod.Networking;

internal static class InboundProtocolPolicy
{
    private static readonly HashSet<string> AllowedFromPython = new(StringComparer.Ordinal)
    {
        ProtocolConstants.MessageTypes.Welcome,
        ProtocolConstants.MessageTypes.Control,
        ProtocolConstants.MessageTypes.LeaseAcquire,
        ProtocolConstants.MessageTypes.LeaseRelease,
        ProtocolConstants.MessageTypes.Request,
        ProtocolConstants.MessageTypes.Ping,
        ProtocolConstants.MessageTypes.Pong,
        ProtocolConstants.MessageTypes.Error,
    };

    public static int StreamCount => AllowedFromPython.Count;

    public static bool IsAllowed(string messageType) => AllowedFromPython.Contains(messageType);
}

internal sealed class InboundSequenceTracker
{
    private readonly Dictionary<string, long> _sequences = new(StringComparer.Ordinal);

    public int Count => _sequences.Count;

    public bool TryAdvance(string messageType, long sequence, out ProtocolError? error)
    {
        error = null;
        if (!InboundProtocolPolicy.IsAllowed(messageType))
        {
            error = new("invalid_direction", $"Message type '{messageType}' is not accepted from the Python peer.");
            return false;
        }

        if (_sequences.TryGetValue(messageType, out var previous) && sequence <= previous)
        {
            error = new("stale_sequence", "Envelope seq must increase within its message stream.");
            return false;
        }

        _sequences[messageType] = sequence;
        return true;
    }

    public void Clear() => _sequences.Clear();
}
