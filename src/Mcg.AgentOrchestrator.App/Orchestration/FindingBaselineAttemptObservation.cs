using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A live attempt already owns selection. Read its authority before deriving its inputs again.
internal static class FindingBaselineAttemptObservation
{
    internal static FailedGoalFindingObservation Pending(string source, string attemptId) =>
        FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
            $"Background {source} focused evidence is running in attempt {attemptId}.")
            with { HoldOwner = ConductorHoldOwner.BackgroundAttempt };

    internal static FailedGoalFindingObservation? Read(ConductorDriver driver, Goal goal, ConductorAutonomyPolicy policy)
    {
        // Another failed task changes recovery authority even if this attempt is still live.
        if (goal.Tasks.Count(task => task.Status == WorkTaskStatus.Failed) != 1) return null;
        var candidateSha = driver.ReadRecoveryCandidateRevision(goal);
        if (!ConductorGitRevisionReader.IsValid(candidateSha)) return null;
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Failed && task.LastVerification is not null))
        {
            if (!WorkerResultBlockers.AssessSubscriptionVerification(task, task.LastVerification!).AllowsCompletion ||
                !WorkerResultBlockers.TryFindReviewFindingRound(task.LastVerification, out var round, out _) ||
                !VerifyingFindingCurrency.IsCurrent(goal, task, task.LastVerification!)) continue;
            var fingerprint = ConductorDriver.BuildFindingRoundFingerprint(task, round);
            var attempt = driver._focusedEvidenceAttemptCoordinator.ReadLiveFindingBaselineAttempt(
                goal.Id, candidateSha!, fingerprint, policy.Name);
            if (attempt is { CandidateEvidenceBeforeBaseline: not null, FocusedExecutionBasisIdentity: not null } &&
                attempt.FocusedExecutionBasisIdentity == ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(driver._getFindingEvidenceEngineSettings(goal)))
                return Pending("finding-baseline-arm", attempt.AttemptId);
        }
        return null;
    }
}
