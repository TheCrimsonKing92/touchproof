using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Only successful observations of stable commit pairs belong in this projector's memo.
internal sealed class GateReadyChangedFilesMemo(
    Func<string, string, string?> resolveCommit,
    Func<GoalId, GoalWorktreeChangedFilesResult> compute)
{
    private const int Capacity = 256;
    private readonly object _sync = new();
    private readonly Dictionary<(string Main, string Goal), GoalWorktreeChangedFilesResult> _results = new();
    private readonly Queue<(string Main, string Goal)> _insertionOrder = new();

    internal GoalWorktreeChangedFilesResult Resolve(string directory, GoalId goalId)
    {
        // Serialize misses as well as hits so concurrent projections share one successful diff.
        lock (_sync)
        {
            var goalReference = "refs/heads/" + GoalWorktrees.BranchName(goalId);
            var mainCommit = resolveCommit(directory, "HEAD");
            var goalCommit = resolveCommit(directory, goalReference);
            if (string.IsNullOrWhiteSpace(mainCommit) || string.IsNullOrWhiteSpace(goalCommit))
                return compute(goalId);

            var key = (mainCommit, goalCommit);
            if (_results.TryGetValue(key, out var remembered)) return remembered;

            var result = compute(goalId);
            // The existing diff reads live refs. Never associate it with a pair that moved.
            if (!result.Succeeded || result.Files is null ||
                !string.Equals(mainCommit, resolveCommit(directory, "HEAD"), StringComparison.Ordinal) ||
                !string.Equals(goalCommit, resolveCommit(directory, goalReference), StringComparison.Ordinal))
                return result;

            var snapshot = result with { Files = Array.AsReadOnly(result.Files.ToArray()) };
            if (_results.Count == Capacity) _results.Remove(_insertionOrder.Dequeue());
            _results.Add(key, snapshot);
            _insertionOrder.Enqueue(key);
            return snapshot;
        }
    }
}
