namespace Mcg.AgentOrchestrator.App.Orchestration;

// Instance-owned completed facts only. Serialize publication with lookup and eviction.
internal sealed class BoundedLruFactCache<TKey, TValue>(int capacity = 1024) where TKey : class
{
    private readonly int _capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object _gate = new();
    private readonly Dictionary<TKey, LinkedListNode<(TKey Key, TValue Value)>> _entries = new();
    private readonly LinkedList<(TKey Key, TValue Value)> _lru = new();
    internal int Count { get { lock (_gate) return _entries.Count; } }

    internal TValue GetOrCompute(TKey? key, Func<TValue> compute, Func<TValue, bool> cacheable,
        Func<TKey?> revalidate, Action<bool>? count = null)
    {
        lock (_gate)
        {
            if (key is not null && _entries.TryGetValue(key, out var node))
            {
                _lru.Remove(node);
                _lru.AddLast(node);
                count?.Invoke(true);
                return node.Value.Value;
            }
            count?.Invoke(false);
            var value = compute();
            if (key is null || !cacheable(value) || !EqualityComparer<TKey>.Default.Equals(key, revalidate())) return value;
            _entries.Add(key, _lru.AddLast((key, value)));
            if (_entries.Count > _capacity)
            {
                _entries.Remove(_lru.First!.Value.Key);
                _lru.RemoveFirst();
            }
            return value;
        }
    }
}
