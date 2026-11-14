using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Yield considers kernel goals independently of admission eligibility and overtake ordering.
internal static class SelectedExecutionBackstopWaitingGoals
{
    internal static IReadOnlyList<Goal> Select(AgentOrchestratorKernel kernel, ConductorDriver driver,
        ConductorAutonomyPolicy policy) => Select(kernel.Goals,
            goal => driver.AdmissionFacts.Active ? driver.ProjectGroupedAdmissionCandidate(goal, policy, kernel) : null,
            goal => driver.ExecutionDirectory is { } directory && AdmissionFactsRefReader.TryResolve(directory,
                $"refs/heads/{GoalWorktrees.BranchName(goal.Id)}", out var candidate) &&
                HasPassedOutcome(goal, driver.ParallelAcceptanceAttemptCoordinator.TryReadLatest(goal.Id.Value), candidate));

    internal static IReadOnlyList<Goal> Select(IEnumerable<Goal> goals,
        Func<Goal, GateReadyCandidateProjectionResult?> project, Func<Goal, bool> hasPassedOutcome) =>
        goals.Where(goal => goal.Status == GoalStatus.Verified &&
            !IsHeldByOpenRequest(project(goal)) && !hasPassedOutcome(goal)).ToArray();

    private static bool IsHeldByOpenRequest(GateReadyCandidateProjectionResult? projection) =>
        projection is GateReadyCandidateProjectionResult.Excluded
            { Reason: GateReadyCandidateExclusionReason.LandingHeld, HoldRequestId: { Length: > 0 } };

    internal static bool HasPassedOutcome(Goal goal, ConductorParallelAcceptanceAttempt? attempt, string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) && attempt is
            { Outcome: ConductorParallelAcceptanceAttemptOutcome.Passed, SupersededBy: null, RegateRequestedAt: null } &&
        attempt.GoalId == goal.Id.Value && attempt.Kind == ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind &&
        string.Equals(attempt.BranchHeadSha, candidate, StringComparison.OrdinalIgnoreCase);
}
