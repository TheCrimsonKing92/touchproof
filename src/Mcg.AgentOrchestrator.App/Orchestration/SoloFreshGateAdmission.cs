using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SoloFreshGateAdmission
{
    internal static bool WasAdmitted(Goal goal, string cause, string candidate, string? main = null) =>
        goal.Timeline.Any(evt => evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith($"SOLO_FRESH_GATE cause={cause} candidate_sha={candidate} " +
                (main is null ? "" : $"main_sha={main} "), StringComparison.Ordinal));

    internal static ConductorAdvanceResult? TryAdmit(AgentOrchestratorKernel? kernel, Goal goal,
        string prefix, ConductorAutonomyPolicy policy, SoloAcceptanceConsumptionContext? current, string cause)
    {
        if (kernel is null || current is null || string.IsNullOrWhiteSpace(current.CandidateSha) ||
            !ReferenceEquals(kernel.GetGoal(goal.Id), goal) ||
            goal.Status != GoalStatus.AcceptanceFailed ||
            goal.Tasks.Any(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) ||
                task.LastProcess is { IsRunning: true })) return null;
        var attempt = goal.LatestAcceptanceFailure?.AttemptId ?? "absent";
        var marker = $"SOLO_FRESH_GATE cause={cause} candidate_sha={current.CandidateSha} main_sha={current.MainSha} attempt={attempt}";
        if (!kernel.RestoreVerifiedForClassifiedAcceptanceFailure(goal.Id, marker))
            throw new InvalidOperationException("Solo fresh gate lost its AcceptanceFailed precondition.");
        return new(goal.Id.Value, prefix, policy.Name,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.AcceptanceFailed, marker)
            {
                Decision = new("solo-acceptance-correction", "FreshGate", 1, cause, marker,
                    [new("candidateSha", current.CandidateSha), new("mainSha", current.MainSha), new("attemptId", attempt)])
            });
    }
}
