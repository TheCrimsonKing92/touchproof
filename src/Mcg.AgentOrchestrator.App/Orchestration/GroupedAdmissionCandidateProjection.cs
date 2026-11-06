using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the grouped projection pipeline and reporting for the candidate set used by both planners.
internal static class GroupedAdmissionCandidateProjection
{
    internal static GateReadyCandidateProjectionResult Project(
        GateReadyCandidateProjectionResult projection, Goal goal,
        IReadOnlyList<HumanInputRequest> pending, string? executionDirectory) =>
        GroupedAdmissionOwnerApprovalHold.Apply(
            GroupedAdmissionLandingHold.Apply(projection, pending, goal), goal, executionDirectory);

    internal static ConductorSpeculativeAcceptanceCandidate[] ProjectAll(
        IEnumerable<Goal> goals, ConductorDriver driver, ConductorAutonomyPolicy policy,
        AgentOrchestratorKernel kernel, int tick, Action<string> recordProgress, IEnumerable<Goal>? additionalFactsGoals = null)
    {
        var observed = goals.ToArray();
        driver.BeginAdmissionFacts(observed.Concat(additionalFactsGoals ?? []).DistinctBy(goal => goal.Id), kernel);
        foreach (var line in driver.AdmissionFacts.Reader?.DrainReports() ?? []) recordProgress(line);
        var candidates = ConductorTickStepLedger.ObserveProjections(observed).Select(goal => new ConductorSpeculativeAcceptanceCandidate(
            goal.Id, driver.ProjectGroupedAdmissionCandidate(goal, policy, kernel))).ToArray();
        GroupedAdmissionOwnerApprovalHold.ReportExclusions(candidates, tick, recordProgress);
        return candidates;
    }
}
