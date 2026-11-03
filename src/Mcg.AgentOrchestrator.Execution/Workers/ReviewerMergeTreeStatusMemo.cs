namespace Mcg.AgentOrchestrator.Infrastructure;

// Successful results belong to immutable commit pairs; insertion order bounds their lifetime.
internal sealed class ReviewerMergeTreeStatusMemo(Func<string, string, string?> resolveCommit)
{
    private const int Capacity = 256;
    private readonly object _sync = new();
    private readonly Dictionary<(string Base, string Head), ReviewerMergeTreeStatus> _results = new();
    private readonly Queue<(string Base, string Head)> _insertionOrder = new();

    internal bool TryResolveKey(string directory, string baseReference, string headReference,
        out (string Base, string Head) key)
    {
        var baseCommit = resolveCommit(directory, baseReference);
        var headCommit = resolveCommit(directory, headReference);
        key = default;
        if (string.IsNullOrWhiteSpace(baseCommit) || string.IsNullOrWhiteSpace(headCommit)) return false;
        key = (baseCommit, headCommit);
        return true;
    }

    internal bool TryGet((string Base, string Head) key, out ReviewerMergeTreeStatus status)
    {
        lock (_sync) return _results.TryGetValue(key, out status!);
    }

    internal void Add((string Base, string Head) key, ReviewerMergeTreeStatus status)
    {
        lock (_sync)
        {
            if (_results.ContainsKey(key))
            {
                _results[key] = status;
                return;
            }
            if (_results.Count == Capacity) _results.Remove(_insertionOrder.Dequeue());
            _results.Add(key, status);
            _insertionOrder.Enqueue(key);
        }
    }
}
