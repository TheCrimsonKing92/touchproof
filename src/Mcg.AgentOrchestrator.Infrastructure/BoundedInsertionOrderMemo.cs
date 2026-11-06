namespace Mcg.AgentOrchestrator.Infrastructure;

// Owns successful values only. Callers compute outside the lock and decide what is cacheable.
internal sealed class BoundedInsertionOrderMemo<TKey, TValue> where TKey : notnull
{
    private readonly object sync = new();
    private readonly Dictionary<TKey, TValue> entries;
    private readonly Queue<TKey> insertionOrder = new();

    internal BoundedInsertionOrderMemo(int capacity, IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        Capacity = capacity;
        entries = new(comparer);
    }

    internal int Capacity { get; }
    internal int Count { get { lock (sync) return entries.Count; } }

    internal bool TryGet(TKey key, out TValue value)
    {
        lock (sync) return entries.TryGetValue(key, out value!);
    }

    internal void Store(TKey key, TValue value)
    {
        lock (sync)
        {
            if (!entries.ContainsKey(key))
            {
                if (entries.Count == Capacity) entries.Remove(insertionOrder.Dequeue());
                insertionOrder.Enqueue(key);
            }
            entries[key] = value;
        }
    }
}
