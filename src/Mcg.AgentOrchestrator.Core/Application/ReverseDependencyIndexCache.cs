using ReverseDependencyIndex = Mcg.AgentOrchestrator.Core.ReverseDependencyTestImpactReader.ReverseDependencyIndex;

namespace Mcg.AgentOrchestrator.Core;

internal sealed class ReverseDependencyIndexCache
{
    private const int MaximumRetainedSnapshots = 4;
    private readonly object _gate = new();
    private readonly Dictionary<string, LinkedListNode<CachedIndex>> _byFingerprint = new(StringComparer.Ordinal);
    private readonly LinkedList<CachedIndex> _recency = new();

    internal bool TryGet(string fingerprint, out ReverseDependencyIndex index, out int retainedSnapshotCount)
    {
        lock (_gate)
        {
            if (!_byFingerprint.TryGetValue(fingerprint, out var node))
            {
                index = null!;
                retainedSnapshotCount = _byFingerprint.Count;
                return false;
            }

            _recency.Remove(node);
            _recency.AddFirst(node);
            index = node.Value.Index;
            retainedSnapshotCount = _byFingerprint.Count;
            return true;
        }
    }

    internal int Retain(string fingerprint, ReverseDependencyIndex index)
    {
        lock (_gate)
        {
            if (_byFingerprint.TryGetValue(fingerprint, out var existing))
            {
                _recency.Remove(existing);
                _recency.AddFirst(existing);
                return _byFingerprint.Count;
            }

            var node = _recency.AddFirst(new CachedIndex(fingerprint, index));
            _byFingerprint.Add(fingerprint, node);
            while (_byFingerprint.Count > MaximumRetainedSnapshots)
            {
                var expired = _recency.Last!;
                _recency.RemoveLast();
                _byFingerprint.Remove(expired.Value.Fingerprint);
            }

            return _byFingerprint.Count;
        }
    }

    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _byFingerprint.Count;
            }
        }
    }

    internal void Clear()
    {
        lock (_gate)
        {
            _byFingerprint.Clear();
            _recency.Clear();
        }
    }

    private sealed record CachedIndex(string Fingerprint, ReverseDependencyIndex Index);
}
