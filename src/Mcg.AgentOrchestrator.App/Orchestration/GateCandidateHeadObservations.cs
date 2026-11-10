using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One instance per tick, including failed observations. Neither delegate launches git.
internal sealed class GateCandidateHeadObservations(
    Func<GoalId, string?> tickSnapshotBranch,
    Func<GoalId, string?> fileRefFallback)
{
    private readonly Dictionary<GoalId, string?> _heads = [];

    internal string? Observe(GoalId goalId)
    {
        if (_heads.TryGetValue(goalId, out var observed)) return observed;
        string? head = null;
        try
        {
            var value = tickSnapshotBranch(goalId) ?? fileRefFallback(goalId);
            if (ConductorGitRevisionReader.TryNormalize(value, out var normalized)) head = normalized;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
        _heads[goalId] = head;
        return head;
    }
}
