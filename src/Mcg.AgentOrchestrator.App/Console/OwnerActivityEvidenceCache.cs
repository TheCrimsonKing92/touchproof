using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Receipt evidence is immutable. Missing evidence and receipt-less observations remain retryable.
internal sealed class OwnerActivityEvidenceCache
{
    private const int Capacity = 512;
    private readonly object _gate = new();
    private readonly Dictionary<Key, OwnerActivityTestEvidence> _entries = new();
    private readonly Queue<Key> _oldest = new();

    internal OwnerActivityTestEvidence? Read(OwnerConductEvent item,
        Func<OwnerConductEvent, OwnerActivityTestEvidence?> read)
    {
        if (Field(item, "receipt") is not { Length: > 0 } receipt) return read(item);
        var key = new Key(item.EventKind, item.GoalId, receipt, Field(item, "identity"),
            Field(item, "attempt"), Field(item, "kind"));
        lock (_gate)
        {
            if (_entries.TryGetValue(key, out var cached)) return cached;
            var result = read(item);
            if (result is null) return null;
            if (_entries.Count == Capacity) _entries.Remove(_oldest.Dequeue());
            _entries.Add(key, result);
            _oldest.Enqueue(key);
            return result;
        }
    }

    private sealed record Key(string EventKind, string? GoalId, string Receipt,
        string? Identity, string? Attempt, string? Kind);
}
