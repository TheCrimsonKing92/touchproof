using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerGoalRangeReadCache(
    Func<string, string, CancellationToken, Task<CommittedGoalChanges>>? changes)
{
    private readonly object _cacheGate = new();
    private readonly SemaphoreSlim _diffRead = new(1, 1);
    private readonly Dictionary<CommittedRange, RangeRead> _ranges = new();
    private CommittedRange? _observedRange;
    private CommittedRange? _wholeRange;
    private long _loadVersion;

    internal sealed record CommittedRange(string Base, string Candidate);

    internal sealed class RangeRead(Task<CommittedGoalChanges> task)
    {
        internal Task<CommittedGoalChanges> Task { get; } = task;
        internal int Users { get; set; }
    }

    internal sealed class RangeRequest(OwnerGoalRangeReadCache owner, long version) : IDisposable
    {
        internal long Version { get; } = version;
        internal bool ObservedResolved { get; set; }
        internal bool WholeResolved { get; set; }
        internal HashSet<RangeRead> Reads { get; } = [];

        public void Dispose()
        {
            lock (owner._cacheGate)
            {
                foreach (var read in Reads) read.Users--;
                if (Version == owner._loadVersion)
                {
                    if (!ObservedResolved) owner._observedRange = null;
                    if (!WholeResolved) owner._wholeRange = null;
                }
                owner.PruneRanges();
            }
        }
    }

    internal RangeRequest BeginRangeRequest()
    {
        lock (_cacheGate) return new(this, ++_loadVersion);
    }

    internal void ResolveRange(RangeRequest request, bool whole, CommittedRange? range)
    {
        lock (_cacheGate)
        {
            if (whole) request.WholeResolved = true;
            else request.ObservedResolved = true;
            // Late continuations may finish their own load, but cannot restore obsolete relevance.
            if (request.Version != _loadVersion) return;
            if (whole) _wholeRange = range;
            else _observedRange = range;
            PruneRanges();
        }
    }

    internal Task<CommittedGoalChanges> SharedChangesAsync(RangeRequest request, CommittedRange range,
        CancellationToken token)
    {
        lock (_cacheGate)
        {
            PruneRanges();
            if (!_ranges.TryGetValue(range, out var read))
            {
                // The initiating caller owns cancellation; other callers cancel only their own wait.
                read = new(ReadChangesAsync(range.Base, range.Candidate, token));
                _ranges.Add(range, read);
            }
            if (request.Reads.Add(read)) read.Users++;
            return read.Task;
        }
    }

    // At rest at most two distinct ranges survive. Superseded loads hold leases only until they exit;
    // their completion never publishes a range back into this dictionary.
    private void PruneRanges()
    {
        foreach (var (range, read) in _ranges.ToArray())
            if ((read.Users == 0 && range != _observedRange && range != _wholeRange) ||
                read.Task.IsCanceled || read.Task.IsFaulted ||
                (read.Task.IsCompletedSuccessfully && !read.Task.Result.Available))
                _ranges.Remove(range);
    }

    private async Task<CommittedGoalChanges> ReadChangesAsync(string basis, string candidate, CancellationToken token)
    {
        await _diffRead.WaitAsync(token).ConfigureAwait(false);
        try { return await changes!(basis, candidate, token).ConfigureAwait(false); }
        finally { _diffRead.Release(); }
    }
}
