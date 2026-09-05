namespace KittenIO.Protocol;

/// <summary>A small session-scoped FIFO cache for replaying acknowledged one-shot requests.</summary>
public sealed class IdempotencyCache<TValue>
{
    private readonly int _capacity;
    private readonly Dictionary<(Guid SessionId, Guid CommandId), TValue> _values = [];
    private readonly Queue<(Guid SessionId, Guid CommandId)> _order = [];
    private readonly object _gate = new();

    public IdempotencyCache(int capacity = 512)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity));
        }

        _capacity = capacity;
    }

    public bool TryGet(Guid sessionId, Guid commandId, out TValue? value)
    {
        lock (_gate)
        {
            return _values.TryGetValue((sessionId, commandId), out value);
        }
    }

    public void Add(Guid sessionId, Guid commandId, TValue value)
    {
        var key = (sessionId, commandId);
        lock (_gate)
        {
            if (_values.ContainsKey(key))
            {
                _values[key] = value;
                return;
            }

            _values.Add(key, value);
            _order.Enqueue(key);
            while (_values.Count > _capacity)
            {
                _values.Remove(_order.Dequeue());
            }
        }
    }

    public void ClearSession(Guid sessionId)
    {
        lock (_gate)
        {
            var survivors = _order.Where(key => key.SessionId != sessionId).ToArray();
            foreach (var key in _order.Where(key => key.SessionId == sessionId))
            {
                _values.Remove(key);
            }

            _order.Clear();
            foreach (var key in survivors)
            {
                _order.Enqueue(key);
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _values.Clear();
            _order.Clear();
        }
    }
}
