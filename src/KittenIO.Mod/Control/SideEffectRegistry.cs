using KittenIO.Mod.Runtime;
using KittenIO.Protocol;

namespace KittenIO.Mod.Control;

/// <summary>
/// Bounded, non-evicting registry for exactly-once game side effects. A logical
/// command remains idempotent for the whole protocol session, including across
/// vehicle and authority-epoch changes. Game/controller/session context keeps
/// unrelated peers independent.
/// </summary>
internal sealed class SideEffectRegistry
{
    public const int MaximumCapacity = 4_096;

    private readonly object _gate = new();
    private readonly Dictionary<(SessionCommandScope Scope, Guid CommandId), ResponsePayload?> _entries = [];
    private readonly int _capacity;

    public SideEffectRegistry(int capacity = MaximumCapacity)
    {
        if (capacity is <= 0 or > MaximumCapacity) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    public SideEffectAdmission TryBegin(CommandContext context, Guid commandId, out ResponsePayload? cached) =>
        TryBegin(CommandScope.From(context), commandId, out cached);

    public SideEffectAdmission TryBegin(CommandScope scope, Guid commandId, out ResponsePayload? cached)
    {
        if (commandId == Guid.Empty) throw new ArgumentException("Command ID must be non-empty.", nameof(commandId));
        lock (_gate)
        {
            var key = (SessionCommandScope.From(scope), commandId);
            if (_entries.TryGetValue(key, out cached))
            {
                return cached is null ? SideEffectAdmission.InProgress : SideEffectAdmission.Replay;
            }

            if (_entries.Count >= _capacity)
            {
                cached = null;
                return SideEffectAdmission.Full;
            }

            _entries.Add(key, null);
            cached = null;
            return SideEffectAdmission.Admitted;
        }
    }

    public void Complete(CommandContext context, Guid commandId, ResponsePayload response) =>
        Complete(CommandScope.From(context), commandId, response);

    public void Complete(CommandScope scope, Guid commandId, ResponsePayload response)
    {
        ArgumentNullException.ThrowIfNull(response);
        lock (_gate)
        {
            var key = (SessionCommandScope.From(scope), commandId);
            if (_entries.ContainsKey(key)) _entries[key] = response;
        }
    }

    public int ClearSession(ProtocolSessionId session) => ClearWhere(key => key.Scope.Session == session);

    public int ClearController(EphemeralControllerId controller) => ClearWhere(key => key.Scope.Controller == controller);

    public int ClearAll()
    {
        lock (_gate)
        {
            var count = _entries.Count;
            _entries.Clear();
            return count;
        }
    }

    public void Clear() => ClearAll();

    private int ClearWhere(Func<(SessionCommandScope Scope, Guid CommandId), bool> predicate)
    {
        lock (_gate)
        {
            var keys = _entries.Keys.Where(predicate).ToArray();
            foreach (var key in keys) _entries.Remove(key);
            return keys.Length;
        }
    }

    private readonly record struct SessionCommandScope(
        GameInstanceId GameInstance,
        EphemeralControllerId Controller,
        ProtocolSessionId Session)
    {
        public static SessionCommandScope From(CommandScope scope) => new(
            scope.GameInstance,
            scope.Controller,
            scope.Session);
    }
}

internal enum SideEffectAdmission
{
    Admitted,
    Replay,
    InProgress,
    Full,
}
