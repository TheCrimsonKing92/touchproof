using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Isolated repositories/stores; the existing loop collection owns build-slot environment state.
[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsMergeEvidenceRelaunch(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    private const string EnginePath = "src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/GoalAcceptanceVerifier.cs";
    private const string DocsPath = "docs/landing.md";

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, false)]
    public async Task Sweep_IntegrateCommit_RecordsDecisionAndRunsMatchingCanary(
        bool engine, bool hasWorkspace, bool relaunchEnabled)
    {
        var root = LandingExecutorTests.CreateGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Recover landed goal");
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            kernel.SetEventWriter(new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory));
            var integrate = Integrate(root, goal, engine ? EnginePath : DocsPath);
            // Later main changes must not participate in the recovered landing's classification.
            LandingExecutorTests.AppendCommit(root, engine ? DocsPath : EnginePath, "later main");
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            var canaryRuns = 0;
            var relaunchCalls = 0;
            var coordinator = Canary(root, request =>
            {
                Assert.Equal(integrate, request.LandingSha);
                Assert.Equal([EnginePath], request.TriggeringPaths);
                Interlocked.Increment(ref canaryRuns);
            });
            TerminalGoalSweepResult? swept = null;
            var persisted = new List<GoalId>();
            var loop = new ConductorBatchLoop(
                measuredSweep: state => swept = TerminalGoalSweep.Run(state, root, "main", goal.Id),
                workspace: hasWorkspace ? workspace : null,
                postLandingCanary: coordinator,
                selfRelaunchEnabled: relaunchEnabled,
                selfRelaunch: _ =>
                {
                    Assert.Contains(goal.Id, persisted);
                    relaunchCalls++;
                    return new ConductorSelfRelaunchResult(false, "build", "test stop");
                });
            var driver = MakeDriver(land: _ => throw new InvalidOperationException("Recovery must not land again."));

            var text = AsyncLocalConsoleRouter.Capture(() => loop.Run(kernel, driver,
                ConductorAutonomyPolicy.Conservative, Path.Combine(root, ".stop"), maxIterations: 1,
                persistGoalTick: (_, ids) => persisted.AddRange(ids)));

            Assert.NotNull(swept);
            Assert.Contains(goal.Id, swept.ExplicitlyTerminalizedGoalIds);
            Assert.Equal(integrate, Assert.Single(swept.Goals).Reconciliation?.IntegrateSha);
            Assert.Equal(GoalStatus.Completed, kernel.GetGoal(goal.Id).Status);
            Assert.Contains(goal.Id, persisted);
            var scheduled = engine && hasWorkspace && relaunchEnabled;
            AssertDecision(text, goal.Id, scheduled);
            Assert.Equal(scheduled ? 1 : 0, relaunchCalls);
            Assert.Equal(engine && hasWorkspace ? 1 : 0, canaryRuns);
            if (!hasWorkspace) Assert.Contains("classification=changed-files-unavailable", text, StringComparison.Ordinal);
            else if (!engine) Assert.Contains("classification=documentation", text, StringComparison.Ordinal);
            else if (!relaunchEnabled) Assert.Contains("classification=self-relaunch-disabled", text, StringComparison.Ordinal);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public async Task Sink_ThenMergeEvidenceSweep_HandlesGoalOnlyOnce()
    {
        var root = LandingExecutorTests.CreateGitRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = CreateVerifiedSimpleGoal(kernel, "Land then recover");
            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var integrate = Integrate(root, goal, EnginePath);
            await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
            var landed = false;
            var canaryRuns = 0;
            var relaunchCalls = 0;
            var sweeps = new List<TerminalGoalSweepResult>();
            var driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                land: candidate =>
                {
                    Assert.False(landed);
                    landed = true;
                    return new LandingResult(candidate.Id.Value, candidate.Id.Value[..8],
                        new LandingDecision.Promote(), "integration", true, "Landed", integrate, [EnginePath]);
                },
                getLandingFileScopes: _ => [EnginePath]);
            var sinkCalls = 0;
            driver.SuccessfulLandingSink = _ => sinkCalls++;
            var loop = new ConductorBatchLoop(
                measuredSweep: state =>
                {
                    if (!landed) return null;
                    var sweep = TerminalGoalSweep.Run(state, root, "main", goal.Id);
                    sweeps.Add(sweep);
                    return sweep;
                },
                workspace: workspace,
                postLandingCanary: Canary(root, _ => Interlocked.Increment(ref canaryRuns)),
                selfRelaunchEnabled: true,
                selfRelaunch: _ =>
                {
                    relaunchCalls++;
                    return new ConductorSelfRelaunchResult(false, "build", "test stop");
                });

            var text = AsyncLocalConsoleRouter.Capture(() => loop.Run(kernel, driver,
                ConductorAutonomyPolicy.Conservative, Path.Combine(root, ".stop"), maxIterations: 2));

            Assert.Equal(1, sinkCalls);
            Assert.Contains(goal.Id, Assert.Single(sweeps).ExplicitlyTerminalizedGoalIds);
            Assert.Equal(integrate, Assert.Single(sweeps[0].Goals).Reconciliation?.IntegrateSha);
            AssertDecision(text, goal.Id, scheduled: true);
            Assert.Equal(1, relaunchCalls);
            Assert.Equal(1, canaryRuns);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void RecoveredDiff_UnknownCommit_ReturnsUnavailable()
    {
        var root = LandingExecutorTests.CreateGitRepository();
        try
        {
            Assert.Null(LandingExecutor.TryResolveRecoveredChangedFiles(root, null, new string('f', 40)));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static string Integrate(string root, Goal goal, string path)
    {
        var branch = GoalWorktrees.BranchName(goal.Id);
        LandingExecutorTests.AddGoalBranchCommit(root, branch, path, "landed behavior");
        var merge = GitCli.Run(root, "merge", "--no-ff", branch, "-m", $"Integrate {branch}");
        Assert.True(merge.Succeeded, merge.Error);
        return LandingExecutorTests.ReadGit(root, "rev-parse", "HEAD");
    }

    private static PostLandingCanaryCoordinator Canary(string root, Action<PostLandingCanaryRequest> observe)
    {
        var path = Path.Combine(root, ".orchestrator", "canary-events.db");
        var events = new PostLandingCanaryEventStore(new SqliteRunEventStore(path), path);
        return new PostLandingCanaryCoordinator(new PostLandingCanaryConfiguration(true, 10, []),
            new PostLandingCanaryRunner(root, runOverride: (request, _) =>
            {
                observe(request);
                return Task.FromResult(PostLandingCanaryOutcome.Passed(1, "test pass"));
            }), events, new AcceptanceEngineCircuitBreaker(events));
    }

    private static void AssertDecision(string text, GoalId goal, bool scheduled)
    {
        var line = Assert.Single(text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith("LOOP_RELAUNCH_SCHEDULED ", StringComparison.Ordinal) ||
                line.StartsWith("LOOP_RELAUNCH_NOT_REQUIRED ", StringComparison.Ordinal)));
        Assert.StartsWith(scheduled ? "LOOP_RELAUNCH_SCHEDULED " : "LOOP_RELAUNCH_NOT_REQUIRED ", line);
        Assert.Contains($"goal={goal.Value}", line, StringComparison.Ordinal);
    }
}
