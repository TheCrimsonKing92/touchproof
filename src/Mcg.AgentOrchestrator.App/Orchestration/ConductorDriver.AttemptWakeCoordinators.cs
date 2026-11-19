using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal ConductorParallelAcceptanceAttemptCoordinator FocusedEvidenceAttemptCoordinator =>
        _focusedEvidenceAttemptCoordinator;

    internal bool TryReconcileFocusedEvidenceAttempt(
        Goal goal,
        ConductorAutonomyPolicy policy,
        string request,
        string? candidateSha,
        string source,
        ConductorFocusedEvidenceRequestContext? requestContext,
        out FocusedEvidenceRunResult evidence,
        out ConductorParallelAcceptanceAttempt? evidenceAttempt,
        out FailedGoalFindingObservation decision, out ConductorParallelAcceptanceAttemptDecisionKind? attemptKind,
        ConductorParallelAcceptanceAttempt? existingAttempt = null) =>
        TryReconcileFocusedEvidenceAttempt(goal, policy, request, candidateSha, source, requestContext,
            out evidence, out evidenceAttempt, out decision, out attemptKind, existingAttempt, deferRoundReconciliation: false);

    private bool TryReconcileFocusedEvidenceAttempt(
        Goal goal,
        ConductorAutonomyPolicy policy,
        string request,
        string? candidateSha,
        string source,
        ConductorFocusedEvidenceRequestContext? requestContext,
        out FocusedEvidenceRunResult evidence,
        out ConductorParallelAcceptanceAttempt? evidenceAttempt,
        out FailedGoalFindingObservation decision, out ConductorParallelAcceptanceAttemptDecisionKind? attemptKind,
        ConductorParallelAcceptanceAttempt? existingAttempt,
        bool deferRoundReconciliation)
    {
        evidence = null!;
        evidenceAttempt = null;
        decision = FailedGoalFindingObservation.None;
        attemptKind = null;
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            slotIndex: 0,
            fileScopes: [],
            branchHeadSha: candidateSha?.Trim(),
            mainHeadSha: null);
        ConductorParallelAcceptanceAttemptDecision attemptDecision;
        try
        {
            attemptDecision = existingAttempt is not null
                ? _focusedEvidenceAttemptCoordinator.ObserveExistingAttempt(existingAttempt, candidate)
                : _focusedEvidenceAttemptCoordinator.EvaluateFocusedEvidence(
                candidate,
                policy,
                request,
                SelectFindingEvidenceRunner(requestContext?.NegativeControl, runBaselineArm: true, requestContext?.RevertPaths, requestContext?.Mutation, requestContext?.RepositoryTarget),
                requestContext);
        }
        catch (Exception ex) when (ex is AcceptanceArtifactWriterLeaseBusyException or PreReviewEvidenceBuildSlotsBusyException)
        {
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingEvidencePending,
                FocusedEvidenceLaunchRefusal.Describe(ex, $"{source} focused-evidence"));
            return false;
        }
        evidenceAttempt = attemptDecision.Attempt;
        attemptKind = attemptDecision.Kind;
        if (attemptDecision.Kind is
            ConductorParallelAcceptanceAttemptDecisionKind.Started or
            ConductorParallelAcceptanceAttemptDecisionKind.Running)
        {
            decision = FindingBaselineAttemptObservation.Pending(source, attemptDecision.Attempt.AttemptId);
            return false;
        }
        if (attemptDecision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun ||
            attemptDecision.Run?.Exception is
                DotnetBuildSlotsBusyException or
                BuildLockBlockedException or
                OperationCanceledException)
        {
            if (!deferRoundReconciliation || attemptDecision.Kind != ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun ||
                !IsFocusedEvidenceExecutionFault(attemptDecision.Attempt.Outcome))
                MarkFocusedEvidenceAttemptIfReady(requestContext, attemptDecision);
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingEvidencePending,
                $"Background {source} focused evidence did not run ({attemptDecision.Attempt.Outcome}); " +
                $"retry on next conduct tick. attempt={attemptDecision.Attempt.AttemptId}: " +
                (attemptDecision.Attempt.Detail ?? "no result artifact was produced"));
            return false;
        }
        if (attemptDecision.Run?.Exception is { } backgroundFailure)
        {
            if (!deferRoundReconciliation)
                MarkFocusedEvidenceAttemptIfReady(requestContext, attemptDecision);
            decision = FailedGoalFindingObservation.Observed(
                FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired,
                $"BACKGROUND_FOCUSED_EVIDENCE_FAILED: {source} focused evidence run failed. " +
                $"attempt={attemptDecision.Attempt.AttemptId}: {backgroundFailure.Message}");
            return false;
        }

        evidence = attemptDecision.Run?.FocusedEvidence ?? new FocusedEvidenceRunResult(
            request,
            Accepted: false,
            Passed: false,
            Summary: $"background {source} focused evidence {attemptDecision.Attempt.Outcome}: " +
                (attemptDecision.Attempt.Detail ?? "no result artifact was produced"),
            Checks: []);
        // Round rejections need a durable charge, and accepted results need durable dispositions.
        // Candidate-only RED instead uses the existing baseline recovery path.
        if (!deferRoundReconciliation || (evidence.Accepted && !CanDeferFindingEvidenceReconciliation(candidateSha!, evidence)))
            MarkFocusedEvidenceAttemptIfReady(requestContext, attemptDecision);
        return true;
    }

    private static bool CanDeferFindingEvidenceReconciliation(string candidateSha, FocusedEvidenceRunResult evidence) =>
        evidence.Accepted && !FindingEvidenceCandidateAttribution.IsCandidateOnlyRed(candidateSha,
            (evidence.Arms ?? []).Select(CreateFindingEvidenceArmReceipt).ToArray(), out _);
}
