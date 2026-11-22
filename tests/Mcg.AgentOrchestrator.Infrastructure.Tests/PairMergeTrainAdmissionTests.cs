using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

using static LandingExecutorTests;

// Parallel-safe: all admission state is in memory and the live census override is scoped to this async context.
public sealed class PairMergeTrainAdmissionTests : ConductorBatchLoopTests
{
    public PairMergeTrainAdmissionTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void CompatibleGoals_AdmitOldestFirstTrainsWithoutCohorts(int count)
    {
        using var scenario = new Scenario(count);

        var summary = scenario.Tick();

        Assert.Equal(count == 5 ? new[] { 3, 2 } : new[] { count },
            scenario.Trains.Select(members => members.Count));
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.Trains.SelectMany(members => members));
        Assert.Equal(0, scenario.CohortCalls);
        Assert.Empty(scenario.SoloGoals);
        Assert.Equal(count, summary.Advanced);
        Assert.Equal(0, summary.Held);
        Assert.Equal(0, summary.Escalated);
    }

    [Fact]
    public void GreenPair_ReturnsBothTrainLandingResults()
    {
        using var scenario = new Scenario(2);

        var summary = scenario.Tick();

        Assert.Equal(scenario.Goals.Select(goal => goal.Id), Assert.Single(scenario.Trains));
        Assert.Equal(2, summary.Advanced);
        Assert.All(scenario.Goals, goal => Assert.Contains(scenario.TickResult!.ProgressLines!,
            line => line.StartsWith("GOAL ", StringComparison.Ordinal) &&
                line.Contains(goal.Id.Value[..8], StringComparison.Ordinal) &&
                line.Contains("result=executed state=Verified", StringComparison.Ordinal)));
        Assert.Contains(scenario.TickResult!.ProgressLines!, line =>
            line.StartsWith("ACCEPTANCE_TRAIN ", StringComparison.Ordinal) &&
            line.Contains("outcome=passed landings=2", StringComparison.Ordinal));
        Assert.Equal(0, scenario.CohortCalls);
        Assert.Empty(scenario.SoloGoals);
    }

    [Fact]
    public void RedPair_OrdinaryFallbackDoesNotRegroupInTheSameTick()
    {
        using var scenario = new Scenario(2, fallback: true);

        scenario.Tick();

        Assert.Equal(scenario.Goals.Select(goal => goal.Id), Assert.Single(scenario.Trains));
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.SoloGoals);
        Assert.Equal(0, scenario.CohortCalls);
    }

    [Fact]
    public void TrainsDisabled_PairUsesOrdinaryAdmissionWithoutCohorts()
    {
        using var scenario = new Scenario(2, trainsEnabled: false);

        scenario.Tick();

        Assert.Empty(scenario.Trains);
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.SoloGoals);
        Assert.Equal(0, scenario.CohortCalls);
    }

    [Fact]
    public void UnrefinedPair_UsesOrdinaryAdmissionWithoutCohorts()
    {
        using var scenario = new Scenario(2, refined: false);

        scenario.Tick();

        Assert.Empty(scenario.Trains);
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.SoloGoals);
        Assert.Equal(0, scenario.CohortCalls);
    }

    [Fact]
    public void OverlappingPair_UsesOrdinaryAdmissionWithoutCohorts()
    {
        using var scenario = new Scenario(2, overlapping: true);

        scenario.Tick();

        Assert.Empty(scenario.Trains);
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.SoloGoals);
        Assert.Equal(0, scenario.CohortCalls);
    }

    [Fact(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    public void RefusedCarryOldestLeavesBothTrainSlotsForEligiblePeers()
    {
        var repository = CreateGitRepository();
        var attemptRoot = CreateTempDirectory("mcg-refused-carry-train");
        try
        {
            AppendCommit(repository, "config/acceptance-manifest.json", """{"environmentContract":{"version":1,"tools":[]}}""");
            var main = ReadGit(repository, "rev-parse", "main");
            ReadGit(repository, "checkout", "-b", "bound");
            AppendCommit(repository, "src/goal.txt", "reviewed goal change");
            var bound = ReadGit(repository, "rev-parse", "HEAD");
            ReadGit(repository, "checkout", "-b", "current", main);
            AppendCommit(repository, "src/goal.txt", "different goal change");
            var branch = ReadGit(repository, "rev-parse", "HEAD");
            ReadGit(repository, "checkout", "main");

            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                attemptRoot, TrunkBranchName.Default, repository, runInline: true);
            using var scenario = new Scenario(3, repository: repository, coordinator: coordinator,
                readHeads: goal => (branch, ReadGit(repository, "rev-parse", "main")));
            var oldest = scenario.Goals[0];
            var obligation = scenario.Kernel.MapCriterionEvidenceOwner(oldest.Id, 0, 1,
                CriterionEvidenceOwner.Acceptance, "reviewer", CriterionEvidenceScopes.FullAcceptanceGate,
                expectedCandidateSha: bound);
            var worktree = GoalWorktrees.WorktreePath(repository, oldest.Id);
            Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
            ReadGit(repository, "worktree", "add", "-b", GoalWorktrees.BranchName(oldest.Id), worktree, branch);

            Assert.Equal(1, scenario.Tick(oldest.Id.Value).Held);
            Assert.Equal(oldest.Id, Assert.Single(scenario.SoloGoals));
            var passed = Assert.IsType<ConductorParallelAcceptanceAttempt>(coordinator.TryReadLatest(oldest.Id.Value));
            Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Passed, passed.Outcome);

            var peers = scenario.Goals.Skip(1).ToArray();
            AppendCommit(GoalWorktrees.Ensure(repository, peers[0].Id),
                "tests/Mcg.AgentOrchestrator.Core.Tests/FirstPeer.cs", "first peer");
            AppendCommit(GoalWorktrees.Ensure(repository, peers[1].Id),
                "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/SecondPeer.cs", "second peer");
            AppendCommit(repository, "src/main.txt", "advanced main");

            var workspace = OrchestratorWorkspace.ForDirectory(repository);
            var verifier = new BackgroundGateStartHarness.ScriptedAcceptanceVerifier(
                new AcceptanceVerificationResult(true, false, 0, null));
            var driver = new ConductorDriver(scenario.Kernel, workspace, verifier, DefaultAgents(),
                WorkerProfileCatalog.Default(), parallelAcceptanceAttemptCoordinator: coordinator,
                cleanupHooks: new GoalWorktreeCleanupHooks
                {
                    BuildStorageRoot = new DotnetBuildStorageRoot(Path.Combine(attemptRoot, "dotnet"))
                });
            var reservations = new List<ConductorAcceptanceCapacitySnapshot>();
            // The production train has registered its capacity root before scheduling the verifier.
            driver.SetCohortGateBackgroundStartForTests(_ => reservations.Add(driver.GetActiveAcceptanceCohortCapacity()));
            BatchTickSummary? tick = null;
            AdmissionFactsTestData.Warm(driver, scenario.Kernel);
            var summary = new ConductorBatchLoop().Run(scenario.Kernel, driver,
                ConductorAutonomyPolicy.Permissive, NoStopPath(), maxIterations: 1, onTick: result => tick = result);

            Assert.Equal(0, summary.Escalated);
            var reservation = Assert.Single(Assert.Single(reservations).ActiveRoots);
            Assert.DoesNotContain(oldest.Id.Value, reservation.MemberGoalIds);
            Assert.True(reservation.MemberGoalIds.SetEquals(peers.Select(goal => goal.Id.Value)));
            Assert.Equal(0, verifier.RunCount);
            Assert.Equal(passed.AttemptId, coordinator.TryReadLatest(oldest.Id.Value)!.AttemptId);
            Assert.Empty(coordinator.GetUnreconciledAttempts(scenario.Goals.Select(goal => goal.Id.Value).ToArray()));
            Assert.Contains(tick!.ProgressLines!, line =>
                line.Contains("result=held reason=criterion-binding-refused-carry", StringComparison.Ordinal) &&
                line.Contains(obligation.Id, StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(attemptRoot);
            TryDeleteDirectory(repository);
        }
    }

    private sealed class Scenario : IDisposable
    {
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal[] Goals { get; }
        internal List<IReadOnlyList<GoalId>> Trains { get; } = [];
        internal List<GoalId> SoloGoals { get; } = [];
        internal int CohortCalls { get; private set; }
        internal BatchTickSummary? TickResult { get; private set; }
        private readonly ConductorDriver _driver;
        private readonly IDisposable _census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);

        internal Scenario(int count, bool fallback = false, bool trainsEnabled = true, bool overlapping = false,
            bool refined = true, string? repository = null,
            ConductorParallelAcceptanceAttemptCoordinator? coordinator = null,
            Func<Goal, (string BranchHeadSha, string MainHeadSha)>? readHeads = null)
        {
            Goals = Enumerable.Range(0, count).Select(index =>
            {
                var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(),
                    $"Update tests/Mcg.AgentOrchestrator.Core.Tests/PairMember{index}.cs");
                PassVerificationAt(Kernel, goal, goal.Tasks.Single(), DateTimeOffset.UnixEpoch.AddSeconds(index));
                return goal;
            }).ToArray();
            if (refined) MergeTrainAdmissionTestContext.Refine(Kernel, Goals);
            var paths = Goals.Select((goal, index) => (goal.Id, Path: (IReadOnlyList<string>)
                [$"tests/Mcg.AgentOrchestrator.Core.Tests/PairMember{(overlapping ? 0 : index)}.cs"]))
                .ToDictionary(item => item.Id, item => item.Path);
            var projector = new GateReadyCandidateProjector(
                id => new GateReadyCandidateRevisionPair(id.Value.PadRight(40, 'b')[..40], new string('a', 40)),
                id => new GateReadyLandingScopeObservation(true, paths[id]),
                (_, _, _) => new GateReadyMergeTreeObservation(true));
            _driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    SoloGoals.Add(goal.Id);
                    var heads = readHeads?.Invoke(goal);
                    return new AcceptanceVerificationSummary(true, [],
                        BranchHeadSha: heads?.BranchHeadSha, MainHeadSha: heads?.MainHeadSha);
                },
                resolveAcceptanceHeads: readHeads is null ? null : goal => readHeads(goal),
                executionDirectory: repository,
                parallelAcceptanceAttemptCoordinator: coordinator,
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                mergeTrainsEnabled: trainsEnabled,
                runAcceptanceCohort: (_, _, _) =>
                {
                    CohortCalls++;
                    throw new InvalidOperationException("New grouped admission must not create a cohort.");
                },
                runMergeTrain: (selection, _, policy) =>
                {
                    Trains.Add(selection.Members.Select(member => member.GoalId).ToArray());
                    return new ConductorMergeTrainRunResult(null,
                        fallback ? new Dictionary<string, ConductorAdvanceResult>() : selection.Members.ToDictionary(
                            member => member.GoalId.Value,
                            member => new ConductorAdvanceResult(member.GoalId.Value, member.GoalId.Value[..8], policy.Name,
                                new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified,
                                    "Landed by merge train receipt fake-passed-pair.")), StringComparer.Ordinal),
                        [], fallback ? "outcome=failed fallback=ordinary" : $"outcome=passed landings={selection.Members.Count}");
                });
        }

        internal BatchLoopSummary Tick(string? onlyGoalId = null) => new ConductorBatchLoop().Run(
            Kernel, _driver, ConductorAutonomyPolicy.Permissive, NoStopPath(), maxIterations: 1,
            onTick: tick => TickResult = tick, onlyGoalId: onlyGoalId);

        public void Dispose() => _census.Dispose();
    }
}
