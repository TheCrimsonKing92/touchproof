using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MultiRepositoryLandingFailedHold
{
    internal const string Kind = "multi-repository-landing-failed";
    internal const string NextAction = "repair-multi-repository-landing";

    internal static ConductorAdvanceOutcome.Held Create(GoalLifecycleState state, MultiRepositoryLandingFailure failure)
    {
        var commits = string.Join(", ", failure.CompensatingCommits.SelectMany(pair => pair.Value.Select(sha => pair.Key + ":" + sha)));
        var repair = string.Join(", ", failure.ManualRepairKeys);
        return new(state, $"{Kind}: {failure.FailingKey}: {failure.Error}. Compensating commits: {commits}. " +
            $"Manual repair required: {repair}. Owner: operator.", Kind + ":" + failure.TupleCanonical)
        {
            Decision = new PolicyDecisionRecord("multi-repository-landing", "Hold", 1, Kind, NextAction,
                [new PolicyDecisionFact("owner", "operator"), new PolicyDecisionFact("failing_repository", failure.FailingKey),
                 new PolicyDecisionFact("landing_error", failure.Error), new PolicyDecisionFact("compensating_commits", commits),
                 new PolicyDecisionFact("manual_repair", repair), new PolicyDecisionFact("repository_tuple", failure.TupleCanonical)])
        };
    }
}
