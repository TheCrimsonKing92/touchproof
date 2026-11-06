using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private repositories/drivers; scoped capacity observations and a chained per-test observer.
public sealed class GroupedAdmissionPrepFactReuseTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void ApprovalHeldPair_SecondPrepHasZeroProjectionAndApprovalMisses()
    {
        using var scenario = new PassedCohortReceiptApprovalHoldTests.Scenario();
        using var capacity = PassedCohortReceiptApprovalHoldTests.Scenario.PauseNewGates();
        var before = scenario.Goals.Select(goal => goal.Timeline.Count).ToArray();
        var first = Admission(scenario.Tick(waitForAdmissionFacts: false));
        Assert.Contains("prep_approval_hold_cache=hits:0,misses:2", first);
        Assert.Contains("admission_facts=ready:0,pending:2", first);
        Assert.Equal(before, scenario.Goals.Select(goal => goal.Timeline.Count));
        var second = Admission(scenario.Tick());
        Assert.Matches(@"prep_projection_cache=hits:[1-9]\d*,misses:0(?: |$)", second);
        Assert.Contains("prep_approval_hold_cache=hits:2,misses:0", second);
        Assert.Contains("prep_merge_conflict_cache=hits:0,misses:0", second);
        Assert.Equal(1, scenario.Verifier.RunCount);
    }

    [Fact]
    public void ConflictExcludedGoal_TwoPrepsReuseProbeAndEmitCounts()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Unchanged conflict");
        using var fixture = new AdditiveConflictGitFixture(goalId: goal.Id);
        using var capacity = PassedCohortReceiptApprovalHoldTests.Scenario.PauseNewGates();
        var branch = fixture.OriginalHead;
        var main = fixture.MainHead;
        var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            classifyRisk: _ => ChangeRiskTier.DocsOnly, isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: new(_ => new(branch, main),
                _ => new(true, ["src/Mcg.AgentOrchestrator.Core/Feature.cs"]),
                (_, _, _) => new(false, ["src/Feature.cs"])));
        driver.GateReadyProjectionHeadsReader = _ => new(branch, main, main);
        driver.PreLandingMergeConflictKeyReader = _ => PreLandingMergeConflictProbeCache.ReadKey(fixture.Repository, goal.Id, [], "main");
        var probes = 0;
        driver.PreLandingMergeConflictProbe = _ =>
        {
            probes++;
            return GoalWorktrees.ProbeAdditiveConflictMerge(fixture.Repository, goal.Id, [], "main");
        };
        var inMergeStep = false;
        var launches = 0;
        var testName = HostCapacitySlotTimingWriter.CurrentTestName();
        var previous = Volatile.Read(ref GitCli.LaunchObserver);
        Action<string> observer = command =>
        {
            previous?.Invoke(command);
            if (inMergeStep && HostCapacitySlotTimingWriter.CurrentTestName() == testName) Interlocked.Increment(ref launches);
        };
        var loop = new ConductorBatchLoop(processCpuTime: () => TimeSpan.Zero, janitorialTimestamp: () => 0)
            .WithStepProbe(step =>
            {
                if (step == "prep-obligation-filter") inMergeStep = true;
                if (step == "prep-merge-conflict") inMergeStep = false;
            });
        string Tick() => ConductorBatchLoopTestsStepLedger.Phase(
            ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver), "acceptance-admission");
        Assert.Same(previous, Interlocked.CompareExchange(ref GitCli.LaunchObserver, observer, previous));
        try
        {
            var first = Tick();
            Assert.Contains("prep_merge_conflict_cache=hits:0,misses:1", first);
            Assert.True(launches > 0, "The cold merge-conflict step must exercise real git.");
            launches = 0;
            kernel.RecordGoalPolicyDecision(goal.Id, "Same conflict observed on the next prep");
            var second = Tick();
            Assert.Contains("prep_merge_conflict_cache=hits:1,misses:0", second);
            Assert.Contains("prep_projection_cache=hits:1,misses:0", second);
            Assert.Equal(0, launches);
            Assert.Equal(1, probes);
            AdditiveConflictGitFixture.Git(fixture.Repository, "commit", "--allow-empty", "-m", "Advance main");
            main = ConductorGitRevisionReader.ReadRequiredCommit(fixture.Repository, "main^{commit}");
            launches = 0;
            Assert.Contains("prep_merge_conflict_cache=hits:0,misses:1", Tick());
            Assert.True(launches > 0, "A moved main must recompute the real merge-conflict step.");
            Assert.Equal(2, probes);
        }
        finally { Interlocked.CompareExchange(ref GitCli.LaunchObserver, previous, observer); }
    }

    private static string Admission(IReadOnlyList<string> lines) => Assert.Single(lines.Where(line =>
        line.StartsWith("PHASE_TIMING ", StringComparison.Ordinal) && line.Contains("phase=acceptance-admission ", StringComparison.Ordinal)));
}
