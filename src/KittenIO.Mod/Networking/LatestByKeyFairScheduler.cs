namespace KittenIO.Mod.Networking;

/// <summary>
/// Bounded scheduler for replaceable latest state and reliable decisions. Each
/// latest-state key gets one round-robin slot; reliable traffic is limited to a
/// burst so sustained acknowledgements cannot starve telemetry.
/// </summary>
internal sealed class LatestByKeyFairScheduler<TKey, TValue>
    where TKey : notnull
{
    public const int DefaultReliableBurstLimit = 16;

    private readonly object _gate = new();
    private readonly int _latestCapacity;
    private readonly int _reliableCapacity;
    private readonly int _reliableBurstLimit;
    private readonly Dictionary<TKey, TValue> _latest = [];
    private readonly Queue<TKey> _latestOrder = [];
    private readonly Queue<TValue> _reliable = [];
    private int _reliableBurst;

    public LatestByKeyFairScheduler(
        int latestCapacity,
        int reliableCapacity,
        int reliableBurstLimit = DefaultReliableBurstLimit)
    {
        if (latestCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(latestCapacity));
        if (reliableCapacity <= 0) throw new ArgumentOutOfRangeException(nameof(reliableCapacity));
        if (reliableBurstLimit <= 0) throw new ArgumentOutOfRangeException(nameof(reliableBurstLimit));
        _latestCapacity = latestCapacity;
        _reliableCapacity = reliableCapacity;
        _reliableBurstLimit = reliableBurstLimit;
    }

    public int LatestCount
    {
        get
        {
            lock (_gate) return _latest.Count;
        }
    }

    public int ReliableCount
    {
        get
        {
            lock (_gate) return _reliable.Count;
        }
    }

    public bool TryEnqueueLatest(TKey key, TValue value)
    {
        lock (_gate)
        {
            if (_latest.ContainsKey(key))
            {
                _latest[key] = value;
                return true;
            }

            if (_latest.Count >= _latestCapacity) return false;
            _latest.Add(key, value);
            _latestOrder.Enqueue(key);
            return true;
        }
    }

    public bool TryEnqueueReliable(TValue value)
    {
        lock (_gate)
        {
            if (_reliable.Count >= _reliableCapacity) return false;
            _reliable.Enqueue(value);
            return true;
        }
    }

    public bool TryDequeue(out TValue? value, out OutboundLane lane)
    {
        lock (_gate)
        {
            if (_reliable.Count > 0 && (_latest.Count == 0 || _reliableBurst < _reliableBurstLimit))
            {
                value = _reliable.Dequeue();
                if (_reliableBurst < _reliableBurstLimit) _reliableBurst++;
                lane = OutboundLane.Reliable;
                return true;
            }

            while (_latestOrder.TryDequeue(out var key))
            {
                if (!_latest.Remove(key, out value)) continue;
                _reliableBurst = 0;
                lane = OutboundLane.Latest;
                return true;
            }

            if (_reliable.TryDequeue(out value))
            {
                if (_reliableBurst < _reliableBurstLimit) _reliableBurst++;
                lane = OutboundLane.Reliable;
                return true;
            }

            value = default;
            lane = default;
            return false;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _latest.Clear();
            _latestOrder.Clear();
            _reliable.Clear();
            _reliableBurst = 0;
        }
    }
}

internal enum OutboundLane
{
    Latest,
    Reliable,
}
