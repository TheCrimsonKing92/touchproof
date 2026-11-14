using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: private directory, fake driver, AsyncLocal console capture, and no worker execution.
public sealed class ConductorLoopStartBackstopBoundsTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void LoopStart_NoWorkspace_IncludesDefaultBackstopBounds()
    {
        using var git = ConductorGitResolution.Use(ConductorGitResolution.NotChecked);
        var root = CreateTempDirectory("backstop-start-bounds");
        try
        {
            var log = AsyncLocalConsoleRouter.Capture(() => new ConductorBatchLoop().Run(
                new AgentOrchestratorKernel(), MakeDriver(), ConductorAutonomyPolicy.Conservative,
                Path.Combine(root, "stop"), maxIterations: 0));

            var start = Assert.Single(log.Split('\n'), line => line.StartsWith("LOOP_START ", StringComparison.Ordinal));
            Assert.Contains("selectedExecutionBackstopMaxUncoveredLandings=5", start.Trim().Split(' '));
            Assert.Contains("selectedExecutionBackstopMaxOldestLandingAgeHours=2", start.Trim().Split(' '));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
