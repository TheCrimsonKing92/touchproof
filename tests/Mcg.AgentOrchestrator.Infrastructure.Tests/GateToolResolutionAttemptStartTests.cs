using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class GateToolResolutionAttemptStartTests
{
    [Fact]
    public void MissingPowershellDefersAttemptBeforeAnyLaneCallback()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Requires Windows bare command resolution.");
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var environment = GateToolResolutionPreflightTests.CreateToolEnvironment(root);
            File.Delete(Path.Combine(environment["SystemRoot"]!, "System32", "WindowsPowerShell", "v1.0", "powershell.exe"));
            var pathLength = environment["PATH"]!.Length;
            var preSlotInvocations = 0;
            var laneInvocations = 0;
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                Path.Combine(root, "attempts"), TrunkBranchName.Default, executionDirectory: root,
                runInline: true,
                tryRunPreSlot: (_, _) =>
                {
                    preSlotInvocations++;
                    return null;
                },
                createGateToolEnvironmentForTests: () => environment);
            var goal = new AgentOrchestratorKernel().CreateGoal("gate tool preflight negative control");
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["src/Widget.cs"]);

            var decision = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Permissive,
                (_, _, _, _, _) =>
                {
                    laneInvocations++;
                    throw new InvalidOperationException("The lane callback ran after a missing tool.");
                });

            Assert.Equal(0, preSlotInvocations);
            Assert.Equal(0, laneInvocations);
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred, decision.Attempt.Outcome);
            var exception = Assert.IsType<AcceptanceInfrastructureDeferredException>(decision.Run?.Exception);
            Assert.Equal("gate-tool-resolution-missing", exception.ReasonCode);
            Assert.Contains("powershell", exception.OutputTail);
            Assert.Contains($"PATH length: {pathLength}", exception.OutputTail);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }
}
