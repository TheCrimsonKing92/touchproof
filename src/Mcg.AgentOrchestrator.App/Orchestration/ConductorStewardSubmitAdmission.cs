using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductorStewardSubmitAdmission
{
    internal static string? Evaluate(Goal goal, TaskSpec task, AdjudicateOperatorIntentPayload payload,
        DateTimeOffset now, Func<GoalId, long?> version)
    {
        if (goal.IsTerminal) return "goal-not-live";
        try
        {
            if (version(goal.Id) is null || goal.IsTerminal) return "goal-not-live";
        }
        catch
        {
            return "goal-not-live";
        }
        if (AdjudicationPrecondition.IsStale(payload, goal, task)) return "stale-precondition";
        return ConductorStewardCaseG.Admits(goal, task, payload, now)
            ? null : "steward-capability-boundary";
    }
}
