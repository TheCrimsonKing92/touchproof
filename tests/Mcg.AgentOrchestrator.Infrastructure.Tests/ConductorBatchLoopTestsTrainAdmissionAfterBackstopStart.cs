using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Isolated repositories, stores and build leases; the collection protects the fairness reset.
[Xunit.Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsTrainAdmissionAfterBackstopStart : AcceptanceCohortWorkflowTests
{
    [Theory]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    public async Task DueBackstop_TrainStartsOnlyWhenWidthRemains(int width, int expectedStarts)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        using var backstopEntered = new ManualResetEventSlim();
        using var releaseBackstop = new ManualResetEventSlim();
        try
        {
            ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
            AddAcceptanceManifest(repo);
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            Assert.Equal(Path.GetFullPath(repo), Path.GetFullPath(workspace.ExecutionDirectory));
            Assert.True(Path.GetFullPath(workspace.RunEventStorePath).StartsWith(
                Path.GetFullPath(repo) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            Assert.True(Path.GetFullPath(AcceptanceSelectedExecutionSessionSuspension.Home(
                workspace.ExecutionDirectory, workspace.ProjectHomeDirectoryOrNull)).StartsWith(
                Path.GetFullPath(repo) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            var kernel = new AgentOrchestratorKernel();
            var verifier = new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
                new AcceptanceVerificationResult(true, false, 0, null,
                    Checks: [new AcceptanceCheckResult("backstop", true, 0, null)]),
                onRun: (_, _) =>
                {
                    backstopEntered.Set();
                    if (!releaseBackstop.Wait(TimeSpan.FromMinutes(2)))
                        throw new TimeoutException("Tick callback did not release the backstop verifier.");
                });
            var driver = new ConductorDriver(kernel, workspace, verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(),
                runAcceptanceAttemptsInCurrentProcess: true,
                cleanupHooks: CreateIsolatedCleanupContext(repo).Hooks)
            {
                MergeTrainsFeatureEnabled = true,
                CohortPartitionStableSlotLeaseSource = (_, _) =>
                    AcceptanceStableSlotTestSupport.CreateFakeStableSlotLease(repo)
            };
            var starts = new BackgroundGateStartHarness();
            starts.Capture(driver);
            var now = DateTimeOffset.Parse("2026-11-14T00:00:00Z");
            var logPath = Path.Combine(repo, "backstop-train-conduct.jsonl");
            var loop = new ConductorBatchLoop(workspace: workspace, utcNow: () => now,
                conductEventLogWriter: new ConductEventLogWriter(logPath));
            var policy = ConductorAutonomyPolicy.Permissive with { AcceptanceWidth = width };
            var stop = Path.Combine(repo, "stop-does-not-exist");

            // Consume the coordinator's initial service tick before making a request due.
            loop.Run(kernel, driver, policy, stop, maxIterations: 1);
            var store = new SelectedExecutionBackstopStore(new SqliteRunEventStore(workspace.RunEventStorePath));
            Assert.Null(await store.ReadAsync());
            var head = RunGitOutput(repo, "rev-parse", "main").Trim();
            var landings = Enumerable.Range(1, SelectedExecutionBackstopYieldBounds.Default.MaxUncoveredLandings)
                .Select(index => new SelectedExecutionBackstopLanding(index, $"landed-{index}", head, Applied: true))
                .ToArray();
            await store.WriteAsync("landing", new SelectedExecutionBackstopState(head,
                LastLandingOrdinal: landings.Length, Landings: landings,
                LandingInstants: landings.Select(landing =>
                    new SelectedExecutionBackstopLandingInstant(landing.Ordinal, now)).ToArray()), now);

            var goals = CreateReadyGoals(kernel, repo);
            Assert.True(driver.MergeTrainsEnabled);
            Assert.All(goals, goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                    driver.ProjectGateReadyCandidate(goal, policy));
            });
            AdmissionFactsTestData.Warm(driver, kernel);
            BatchTickSummary? observed = null;
            SelectedExecutionBackstopRequest? started = null;
            var enteredVerifier = false;
            loop.Run(kernel, driver, policy, stop, maxIterations: 1, onTick: summary =>
            {
                try
                {
                    observed = summary;
                    started = store.ReadAsync().GetAwaiter().GetResult()?.Request;
                    enteredVerifier = backstopEntered.Wait(TimeSpan.FromMinutes(1));
                }
                finally { releaseBackstop.Set(); }
            });

            Assert.True(enteredVerifier, "The same-tick backstop did not enter its verifier.");
            Assert.NotNull(started);
            Assert.Equal(1, started.Attempts);
            var tick = Assert.IsType<BatchTickSummary>(observed);
            Assert.Equal(1, tick.Tick);
            var events = File.ReadAllLines(logPath);
            Assert.Contains(events, line => line.Contains(
                "SELECTED_EXECUTION_BACKSTOP result=started", StringComparison.Ordinal));
            Assert.Equal(expectedStarts, starts.StartCount);
            Assert.Equal(expectedStarts, starts.PendingCount);
            Assert.Equal(1, verifier.RunCount);
            var progress = tick.ProgressLines ?? [];
            if (expectedStarts == 0)
            {
                Assert.DoesNotContain(progress, line => line.Contains("ACCEPTANCE_TRAIN", StringComparison.Ordinal));
                Assert.Empty(driver.GetActiveCohortGateMemberGoalIds());
                Assert.Equal(goals.Length, tick.Held);
                Assert.All(goals, goal =>
                {
                    Assert.Equal(GoalStatus.Verified, goal.Status);
                    Assert.Contains(progress, line =>
                        line.Contains($"GOAL goal={goal.Id.Value[..8]} result=held state=Verified", StringComparison.Ordinal) &&
                        line.Contains("reason=acceptance_width_1_reached;", StringComparison.Ordinal) &&
                        line.Contains("backstop:" + started.Id, StringComparison.Ordinal));
                });
                Assert.Empty(driver.ParallelAcceptanceAttemptCoordinator.GetUnreconciledAttempts(
                    goals.Select(goal => goal.Id.Value).ToArray()));
            }
            else
            {
                Assert.Contains(progress, line =>
                    line.Contains("ACCEPTANCE_TRAIN tick=1 members=", StringComparison.Ordinal) &&
                    line.Contains("outcome=inflight", StringComparison.Ordinal));
                Assert.Equal(goals.Length, driver.GetActiveCohortGateMemberGoalIds().Count);
            }
        }
        finally
        {
            releaseBackstop.Set();
            ConductorBatchLoop.ResetParallelAcceptanceFairnessForTests();
            DeleteDirectory(repo);
        }
    }

    private static Goal[] CreateReadyGoals(AgentOrchestratorKernel kernel, string repo)
    {
        var paths = new[]
        {
            "tests/Mcg.AgentOrchestrator.Core.Tests/BackstopFirst.cs",
            "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/BackstopSecond.cs",
            "tests/Mcg.AgentOrchestrator.Dashboard.Tests/BackstopThird.cs"
        };
        return paths.Select((path, index) =>
        {
            var goal = CreateCompletedGoal(kernel, $"Backstop train member {index}", repo);
            var candidate = CreateWorktreeCandidate(repo, goal.Id, path, $"backstop member {index}");
            kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
            {
                AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"]
            });
            kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1,
                CriterionEvidenceOwner.Acceptance, "test",
                CriterionEvidenceScopes.FullAcceptanceGate, expectedCandidateSha: candidate);
            return goal;
        }).ToArray();
    }
}
