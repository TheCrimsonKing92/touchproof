using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private ConductorConflictIntegration? _conflictIntegration;
    private ConductorAdvanceResult EscalateOrBeginConflictIntegration(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, GoalLifecycleState state, PolicyDecisionRecord decision)
    {
        if (_conflictIntegration is not null && LandingDecisionFacts.IsConflict(decision.Reason))
        {
            var attempt = _conflictIntegration.TryRecord(goal);
            if (attempt.Started)
                return MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Executed(state, attempt.Detail));

            decision = LandingDecisionFacts.WithIntegrationRefusal(decision, attempt.Detail);
        }

        var result = Escalate(goal, goalPrefix, policy, state, decision.Reason);
        return result with { Outcome = ((ConductorAdvanceOutcome.Escalated)result.Outcome) with { Decision = decision } };
    }

    private ConductorAdvanceResult? CreateLandingRebaseOutcome(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, string phase,
        bool applySideEffects, GoalWorktreeRebaseResult rebase,
        out ConductorParallelAcceptanceEarlyOutcome? earlyOutcome)
    {
        earlyOutcome = null;
        var decision = LandingRebasePolicy.Evaluate(new LandingRebaseFacts(
            phase, rebase.UpdatedBranch, rebase.Status.ToString(), Array.AsReadOnly(rebase.ConflictFiles.ToArray()),
            rebase.Message, applySideEffects));
        if (decision.Action == LandingRebaseAction.Proceed)
            return null;
        if (decision.Action == LandingRebaseAction.Retire)
        {
            earlyOutcome = ConductorParallelAcceptanceEarlyOutcome.MissingBranchRetired(GoalLifecycleState.CleanedUp, decision.Reason);
            if (applySideEffects)
                _recordMissingBranchRetirement(goal, decision.Reason);
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp) { Decision = decision.ToRecord() });
        }
        earlyOutcome = ConductorParallelAcceptanceEarlyOutcome.PreLandingEscalated(GoalLifecycleState.Verified, decision.Reason);
        if (!applySideEffects)
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, decision.Reason) { Decision = decision.ToRecord() });

        var result = EscalateOrBeginConflictIntegration(goal, goalPrefix, policy, GoalLifecycleState.Verified, decision.ToRecord());
        if (result.Outcome is ConductorAdvanceOutcome.Executed)
            earlyOutcome = null;
        return result;
    }

    private static LandingCompletionFacts ObserveLandingCompletion(AcceptanceVerificationSummary acceptance) => new()
    {
        ApparatusRun = false, BranchSha = acceptance.BranchHeadSha, MainSha = acceptance.MainHeadSha,
        RunPassed = acceptance.Passed, UnmetCriteriaCount = acceptance.RequiredUnmetCriteria.Count
    };

    private ConductorAdvanceResult CreateApparatusCompletionHold(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance,
        string? branchHeadSha, string? mainHeadSha)
    {
        var decision = LandingCompletionPolicy.Evaluate(ObserveLandingCompletion(acceptance) with
        {
            ApparatusRun = true, BranchSha = branchHeadSha, MainSha = mainHeadSha,
            ApparatusCandidate = FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)
        });
        RecordEscalation(goal, GoalLifecycleState.Verified, decision.Reason);
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason, decision.StableIdentity)
            { Decision = decision.ToRecord() });
    }

    private ConductorAdvanceResult CreateFailedAcceptanceCompletion(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance, bool timedOut) =>
        CreateLandingCompletionEscalation(goal, goalPrefix, policy, ObserveLandingCompletion(acceptance) with
        { TimedOut = timedOut, FailureTail = FormatFailureTail(acceptance.FailureDetail) });

    private static ConductorAdvanceOutcome.Held AttachCriterionEvidenceCompletionDecision(
        AcceptanceVerificationSummary acceptance, ConductorAdvanceOutcome.Held hold)
    {
        var decision = LandingCompletionPolicy.Evaluate(ObserveLandingCompletion(acceptance) with
        { EvidenceHoldReason = hold.Reason, EvidenceHoldState = hold.State, EvidenceHoldIdentity = hold.StableIdentity });
        return hold with { Decision = decision.ToRecord() };
    }

    private ConductorAdvanceResult CreateUnmetCriteriaCompletion(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance,
        string criteria, bool retryTaskAvailable) =>
        CreateLandingCompletionEscalation(goal, goalPrefix, policy, ObserveLandingCompletion(acceptance) with
        {
            UnmetCriteria = criteria, RetryTaskAvailable = retryTaskAvailable,
            RetryCount = goal.AutomaticAcceptanceRetryCount, RetryBudget = policy.MaxCriterionRetries
        });

    private ConductorAdvanceResult CreateLandingCompletionEscalation(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, LandingCompletionFacts facts)
    {
        var decision = LandingCompletionPolicy.Evaluate(facts);
        if (decision.Action != LandingCompletionAction.Escalate)
            throw new InvalidOperationException("Landing completion escalation requires an escalate decision.");
        var result = Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, decision.Reason, decision.EscalationKind);
        return result with { Outcome = ((ConductorAdvanceOutcome.Escalated)result.Outcome) with { Decision = decision.ToRecord() } };
    }

    private ConductorAdvanceResult CreateLandingMutationCompletionHold(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance, string mutationBlockReason)
    {
        var decision = LandingCompletionPolicy.Evaluate(ObserveLandingCompletion(acceptance) with { MutationBlockReason = mutationBlockReason });
        return MakeResult(goal.Id.Value, goalPrefix, policy,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason) { Decision = decision.ToRecord() });
    }

    // Single landing-result mapping seam shared with the next landing-decision slice.
    private ConductorAdvanceResult? CreateLandingResultCompletionOutcome(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, AcceptanceVerificationSummary acceptance, LandingResult landResult)
    {
        var escalate = landResult.Decision as LandingDecision.Escalate;
        var kind = escalate is null ? "promote"
            : PullRequestLandingDelivery.IsDeliveredForReview(escalate.Reason) ? "delivered-for-review"
            : LandingExecutor.IsMutationHoldEscalation(escalate.Reason) ? "mutation-hold"
            : LandingExecutor.IsOwnershipHoldEscalation(escalate.Reason) ? "ownership-hold" : "escalate";
        var decision = LandingCompletionPolicy.Evaluate(ObserveLandingCompletion(acceptance) with
        { LandingResultKind = kind, LandingReason = escalate?.Reason });
        if (decision.Action == LandingCompletionAction.Proceed)
            return null;
        if (decision.Action == LandingCompletionAction.Hold)
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, decision.Reason)
                {
                    Owner = kind == "delivered-for-review" ? ConductorHoldOwner.PullRequestReview : ConductorHoldOwner.None,
                    Decision = landResult.Decision.Decision ?? decision.ToRecord()
                });
        if (kind == "ownership-hold")
            return MakeResult(goal.Id.Value, goalPrefix, policy,
                new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, decision.Reason) { Decision = landResult.Decision.Decision ?? decision.ToRecord() });
        var result = Escalate(goal, goalPrefix, policy, GoalLifecycleState.Verified, decision.Reason);
        return result with { Outcome = ((ConductorAdvanceOutcome.Escalated)result.Outcome) with { Decision = landResult.Decision.Decision ?? decision.ToRecord() } };
    }
}
