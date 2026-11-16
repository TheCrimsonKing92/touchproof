using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SoloFreshGateAdmission
{
    internal static ConductorAdvanceResult? TryConsume(AgentOrchestratorKernel? kernel, Goal goal,
        string prefix, ConductorAutonomyPolicy policy, GateReadyCandidateRevisionPair? current,
        Action<Goal, GoalLifecycleState, string> recordEscalation, string? cause = null)
    {
        if (goal.LatestAcceptanceFailure is not { } failure ||
            goal.Status is not (GoalStatus.AcceptanceFailed or GoalStatus.Verified or GoalStatus.Active)) return null;
        // Active repair work owns its normal dispatch flow until every task is ready for acceptance.
        if (goal.Status == GoalStatus.Active &&
            goal.Tasks.Any(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled))) return null;
        // Historical records without an attempt or pair leave Verified work on the normal
        // required-gate path. They cannot authorize task repair or prove supersession.
        if (goal.Status == GoalStatus.Verified && failure.AttemptId is null && failure.SoloFailureReceipt is null &&
            string.IsNullOrWhiteSpace(failure.BranchHeadSha) && string.IsNullOrWhiteSpace(failure.MainHeadSha) &&
            !string.IsNullOrWhiteSpace(current?.BranchRevision) && !string.IsNullOrWhiteSpace(current?.MainRevision))
            return null;
        var currency = AttributedFailureDeveloperRoute.ClassifyAcceptanceFailure(failure,
            current?.BranchRevision, current?.MainRevision);
        if (currency == AcceptanceFailurePairCurrency.Currency.Current) return null;
        var reason = AcceptanceFailurePairCurrency.Unknown;
        if (currency == AcceptanceFailurePairCurrency.Currency.Superseded)
        {
            cause ??= AcceptanceFailurePairCurrency.Superseded;
            if (!WasAdmitted(goal, cause, current!.BranchRevision!, current.MainRevision) &&
                TryRestore(kernel, goal, prefix, policy, current.BranchRevision!, current.MainRevision!, cause) is { } admitted)
                return admitted;
            reason = AcceptanceFailurePairCurrency.NotReady;
        }
        var facts = new PolicyDecisionFact[]
        {
            new("recordedBranchSha", failure.BranchHeadSha ?? "absent"),
            new("recordedMainSha", failure.MainHeadSha ?? "absent"),
            new("currentBranchSha", current?.BranchRevision ?? "absent"),
            new("currentMainSha", current?.MainRevision ?? "absent"),
            new("attemptId", failure.AttemptId ?? "absent")
        };
        var missing = facts.Take(4).Where(fact => fact.Value == "absent" || string.IsNullOrWhiteSpace(fact.Value))
            .Select(fact => fact.Name);
        var detail = $"Acceptance failure consumption held: {reason}; missing revisions={string.Join(',', missing)}; " +
            "owner: operator; next action: restore the complete current revision pair and verify task readiness before re-gating.";
        recordEscalation(goal, GoalLifecycleState.AcceptanceFailed, detail);
        return new(goal.Id.Value, prefix, policy.Name,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.AcceptanceFailed, detail)
            { Decision = new(AcceptanceFailurePairCurrency.Stage, "Hold", 1, reason, detail, facts) });
    }

    internal static bool WasAdmitted(Goal goal, string cause, string candidate, string? main = null) =>
        goal.Timeline.Any(evt => evt.Kind == ProgressKind.GoalPolicyDecision &&
            evt.Message.StartsWith($"SOLO_FRESH_GATE cause={cause} candidate_sha={candidate} " +
                (main is null ? "" : $"main_sha={main} "), StringComparison.OrdinalIgnoreCase));

    internal static ConductorAdvanceResult? TryAdmit(AgentOrchestratorKernel? kernel, Goal goal,
        string prefix, ConductorAutonomyPolicy policy, SoloAcceptanceConsumptionContext? current, string cause)
    {
        if (current is null) return null;
        return TryRestore(kernel, goal, prefix, policy, current.CandidateSha, current.MainSha, cause);
    }

    private static ConductorAdvanceResult? TryRestore(AgentOrchestratorKernel? kernel, Goal goal,
        string prefix, ConductorAutonomyPolicy policy, string candidate, string main, string cause)
    {
        var failure = goal.LatestAcceptanceFailure;
        var currency = AcceptanceFailurePairCurrency.Classify(failure?.BranchHeadSha, failure?.MainHeadSha, candidate, main);
        if (kernel is null || currency == AcceptanceFailurePairCurrency.Currency.Unknown ||
            !ReferenceEquals(kernel.GetGoal(goal.Id), goal) ||
            currency == AcceptanceFailurePairCurrency.Currency.Current && goal.Status != GoalStatus.AcceptanceFailed ||
            WasAdmitted(goal, cause, candidate, main) ||
            goal.Tasks.Any(task => task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) ||
                task.LastProcess is { IsRunning: true })) return null;
        var attempt = goal.LatestAcceptanceFailure?.AttemptId ?? "absent";
        var marker = $"SOLO_FRESH_GATE cause={cause} candidate_sha={candidate} main_sha={main} attempt={attempt}";
        if (currency == AcceptanceFailurePairCurrency.Currency.Superseded)
        {
            if (!kernel.RestoreVerifiedForSupersededAcceptanceFailure(goal.Id, candidate, main)) return null;
            kernel.RecordGoalPolicyDecision(goal.Id, marker);
        }
        else if (!kernel.RestoreVerifiedForClassifiedAcceptanceFailure(goal.Id, marker))
            throw new InvalidOperationException("Solo fresh gate lost its AcceptanceFailed precondition.");
        return new(goal.Id.Value, prefix, policy.Name,
            new ConductorAdvanceOutcome.Executed(GoalLifecycleState.AcceptanceFailed, marker)
            {
                Decision = new("solo-acceptance-correction", "FreshGate", 1, cause, marker,
                    [new("candidateSha", candidate), new("mainSha", main), new("attemptId", attempt)])
            });
    }
}
