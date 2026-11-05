using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Fixture construction only; all resources are supplied by the caller.
internal static class ConductorDriverTestHelpers
{
    internal static ConductorParallelAcceptanceAttemptCoordinator SeedLiveAcceptanceAttempt(
        string root,
        Goal goal)
    {
        var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
            Path.Combine(root, "acceptance-attempts"), Mcg.AgentOrchestrator.Infrastructure.TrunkBranchName.Default,
            isProcessAlive: _ => true,
            launchOwnedProcess: _ => new ConductorParallelAcceptanceOwnedProcessLaunchResult(4242));
        var candidate = ConductorParallelAcceptanceCandidate.Create(
            goal,
            0,
            ["src/CurrentAttempt.cs"],
            "branch-current",
            "main-current");
        var decision = coordinator.Evaluate(
            candidate,
            ConductorAutonomyPolicy.Conservative,
            (_, _, _, _, _) => throw new InvalidOperationException("Owned acceptance callback should not run inline."));

        Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Started, decision.Kind);
        Assert.True(coordinator.HasLiveAttempt(goal.Id.Value));
        return coordinator;
    }
}
