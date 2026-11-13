using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Matches the loop harness's shared infrastructure isolation; census hooks are never installed here.
[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsTickGitCensus(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CompletedTick_EmitsExactlyOneCensusIncludingZeroLaunches(bool launch)
    {
        var root = CreateTempDirectory("tick-git-census");
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var (kernel, _) = SimpleGoal();
            var clock = new FixtureClock();
            using var census = new GitLaunchWindowCensus();
            var sweeps = 0;
            var loop = new ConductorBatchLoop(workspace: workspace,
                conductEventLogWriter: new ConductEventLogWriter(workspace.ConductEventsLogPath),
                loadTrxCoherenceVerdicts: _ => { }, measuredSweep: _ =>
                {
                    sweeps++;
                    if (launch)
                    {
                        var sink = census.BeginTiming("status", "Example.Read");
                        census.RecordLaunch("status", "Example.Read");
                        Assert.NotNull(sink);
                        sink(new("status", 0, 12.5, null));
                    }
                    clock.Milliseconds += 80;
                    return null;
                }).WithDiagnosticTimeProvider(clock).WithGitCensus(census);
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true), getRunningCount: () => 100);
            loop.Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"), maxIterations: 1);
            Assert.Equal(1, sweeps);
            var events = File.ReadAllLines(workspace.ConductEventsLogPath).Select(line => JsonDocument.Parse(line)).ToArray();
            try
            {
                var entry = Assert.Single(events, item => item.RootElement.GetProperty("eventKind").GetString() == "tick-git-census");
                Assert.True(GitLaunchWindowCensus.TryParseTick(entry.RootElement.GetProperty("detail").GetString()!, out var tick));
                Assert.NotNull(tick);
                Assert.Equal(1, tick.Number);
                Assert.Equal(80, tick.ElapsedMs);
                Assert.Equal(launch ? 1 : 0, tick.Launches);
                Assert.Equal(launch ? 12.5 : 0, tick.WallMs);
                Assert.Equal(launch ? 1 : 0, tick.Timed);
                if (launch)
                {
                    Assert.Equal(1, tick.CallSites["Example.Read"]);
                    Assert.Equal(1, tick.Subcommands["status"]);
                }
                else { Assert.Empty(tick.CallSites); Assert.Empty(tick.Subcommands); }
            }
            finally { foreach (var entry in events) entry.Dispose(); }
        }
        finally { Directory.Delete(root, true); }
    }

    private sealed class FixtureClock : TimeProvider
    {
        internal long Milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => Milliseconds;
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch.AddMilliseconds(Milliseconds);
    }
}
