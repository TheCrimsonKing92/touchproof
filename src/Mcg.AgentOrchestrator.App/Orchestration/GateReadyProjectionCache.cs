using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Holds only the latest revision-bound inner projection for each eligible goal.
// Human-input/approval holds belong to the live pipeline outside this cache.
internal sealed class GateReadyProjectionCache
{
    internal sealed record Heads(string BranchRevision, string MainRevision, string ExecutionHeadRevision);
    internal sealed record Key(GoalId GoalId, string GoalStateToken, Heads Revisions,
        string PolicyIdentity, GoalLifecycleFacts Facts);
    private readonly object _gate = new();
    private readonly Dictionary<GoalId, (Key Key, GateReadyCandidateProjectionResult Result)> _entries = new();

    internal int Count { get { lock (_gate) return _entries.Count; } }

    internal static GateReadyCandidateProjectionResult ProjectUncached(
        Goal goal,
        ConductorAutonomyPolicy policy,
        Func<Goal, GoalLifecycleFacts> getFacts,
        Func<Goal, bool> isVerificationGateSatisfied,
        Func<Goal, ChangeRiskTier?> classifyChangeRisk,
        GateReadyCandidateProjector? projector)
    {
        GoalLifecycleState lifecycleState;
        try
        {
            lifecycleState = GoalLifecycle.ResolveState(goal, getFacts(goal));
        }
        catch
        {
            return new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.LifecycleNotReady);
        }

        bool gateSatisfied;
        try
        {
            gateSatisfied = isVerificationGateSatisfied(goal);
        }
        catch
        {
            return new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.GateNotReady);
        }

        ChangeRiskTier? changeRiskTier;
        ConductorTransitionDecision? autoPromotionDisposition;
        try
        {
            changeRiskTier = classifyChangeRisk(goal);
            autoPromotionDisposition = changeRiskTier.HasValue
                ? policy.GetTransitionDecision(GoalLifecycleState.Merged, changeRiskTier.Value)
                : null;
        }
        catch
        {
            changeRiskTier = null;
            autoPromotionDisposition = null;
        }

        var input = new GateReadyCandidateInput(
            goal.Id,
            lifecycleState,
            gateSatisfied,
            changeRiskTier,
            autoPromotionDisposition);
        return projector?.Project(input) ??
            new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.RevisionUnknown);
    }

    internal static Key CreateKey(Goal goal, ConductorAutonomyPolicy policy, Heads heads, GoalLifecycleFacts facts) =>
        new(goal.Id, Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(goal.ToSnapshot()))),
            heads, JsonSerializer.Serialize(new
            {
                policy.Name,
                Decisions = Enum.GetValues<ChangeRiskTier>()
                    .Select(tier => policy.GetTransitionDecision(GoalLifecycleState.Merged, tier)).ToArray()
            }), facts);

    internal GateReadyCandidateProjectionResult GetOrProject(GoalId goalId, Key? key,
        Func<GateReadyCandidateProjectionResult> project, Func<Key?> revalidate)
    {
        lock (_gate)
        {
            if (key is not null && _entries.TryGetValue(goalId, out var entry) && entry.Key == key)
            {
                ConductorTickStepLedger.CountProjectionCache(hit: true);
                return entry.Result;
            }

            ConductorTickStepLedger.CountProjectionCache(hit: false);
            _entries.Remove(goalId);
            var result = project();
            // Do not bind a result to refs/facts that moved while the projector was running.
            if (key is not null && IsCacheable(result, key) && revalidate() == key)
                _entries[goalId] = (key, result);
            return result;
        }
    }

    internal IEnumerable<Goal> Retain(IEnumerable<Goal> eligible, IEnumerable<Goal> ordered)
    {
        var ids = eligible.Select(goal => goal.Id).ToHashSet();
        lock (_gate)
            foreach (var id in _entries.Keys.Where(id => !ids.Contains(id)).ToArray())
                _entries.Remove(id);
        return ordered;
    }

    private static bool IsCacheable(GateReadyCandidateProjectionResult result, Key key) => result switch
    {
        GateReadyCandidateProjectionResult.Ready ready =>
            ready.Projection.GoalId == key.GoalId &&
            ready.Projection.BranchRevision == key.Revisions.BranchRevision &&
            ready.Projection.MainRevision == key.Revisions.MainRevision,
        // LifecycleNotReady and GateNotReady can also mean a caught exception, so retry them live.
        GateReadyCandidateProjectionResult.Excluded excluded => excluded.Reason is
            GateReadyCandidateExclusionReason.RiskNotAutoPromotable or
            GateReadyCandidateExclusionReason.ScopeEmpty or
            GateReadyCandidateExclusionReason.ResourcesEmpty or
            GateReadyCandidateExclusionReason.MergeConflict,
        _ => false
    };
}
