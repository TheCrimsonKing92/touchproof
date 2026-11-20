using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the plan and verdict for one goal at one instant; callers own the hold scope.
internal static class DispatchReadinessAssessment
{
    public static DispatchReadinessAssessmentResult Evaluate(
        Goal goal,
        IReadOnlyCollection<Goal>? providerHoldScope,
        IReadOnlyList<AgentDefinition> agents,
        WorkerProfileCatalog profiles,
        DateTimeOffset now,
        WorkerSandboxOptions? sandboxOptions = null,
        Func<string, bool>? commandExists = null,
        Func<ClaudeCliAuthState>? claudeAuthProbe = null)
    {
        var plan = SubscriptionPlanBuilder.Build(
            goal, agents, profiles, now: now, providerHoldScope: providerHoldScope,
            sandboxOptions: sandboxOptions, commandExists: commandExists, claudeAuthProbe: claudeAuthProbe);
        return new(plan, DispatchReadinessEvaluator.EvaluateDispatchReadiness(goal, plan, now));
    }

    internal static bool IsCandidateGoal(Goal goal) =>
        goal.Status is GoalStatus.Active or GoalStatus.WaitingForHuman;

    internal static IReadOnlyList<SubscriptionPlanItem> ReadyItems(DispatchReadinessAssessmentResult assessment) =>
        assessment.Verdict is DispatchReadinessReady
            ? assessment.Plan.Items.Where(item => item.TaskStatus == WorkTaskStatus.Assigned &&
                (item.CanPrepare || item.CanAttemptSubscriptionSubstitution)).ToArray()
            : [];
}

internal sealed record DispatchReadinessAssessmentResult(
    SubscriptionPlan Plan,
    DispatchReadinessVerdict Verdict);
