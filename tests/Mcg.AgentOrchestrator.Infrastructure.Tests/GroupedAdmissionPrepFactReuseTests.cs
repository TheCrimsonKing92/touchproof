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
        => VerifyConflictPrep(injectCachedProbe: false);

    [Fact]
    public void ConflictExcludedGoal_CachedPrepProbeCallFailsLaunchAssertionNamingProbeCommand()
        => VerifyConflictPrep(injectCachedProbe: true);

    private void VerifyConflictPrep(bool injectCachedProbe)
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
        var previousProbe = driver.PreLandingMergeConflictProbe;
        var inProbe = new AsyncLocal<bool>();
        driver.PreLandingMergeConflictProbe = _ =>
        {
            var previousScope = inProbe.Value;
            inProbe.Value = true;
            try
            {
                probes++;
                return GoalWorktrees.ProbeAdditiveConflictMerge(fixture.Repository, goal.Id, [], "main");
            }
            finally { inProbe.Value = previousScope; }
        };
        // Bind inherited work to its originating prep, even if it finishes during a later tick.
        var measurement = new AsyncLocal<PrepGitLaunches?>();
        var launches = new PrepGitLaunches();
        var testName = HostCapacitySlotTimingWriter.CurrentTestName();
        var previous = Volatile.Read(ref GitCli.LaunchObserver);
        Action<string> observer = command =>
        {
            previous?.Invoke(command);
            if (HostCapacitySlotTimingWriter.CurrentTestName() == testName)
            {
                var current = Volatile.Read(ref launches);
                current.Record(command, inProbe.Value && ReferenceEquals(measurement.Value, current) && IsProbeCommand(command));
            }
        };
        var prepNumber = 0;
        var loop = new ConductorBatchLoop(processCpuTime: () => TimeSpan.Zero, janitorialTimestamp: () => 0)
            .WithStepProbe(step =>
            {
                if (step == "prep-obligation-filter") measurement.Value?.SetWindow(open: true);
                if (step == "prep-merge-conflict")
                {
                    // Bypass the cache only in the negative control, before closing its window.
                    if (injectCachedProbe && prepNumber == 2) driver.PreLandingMergeConflictProbe(goal);
                    measurement.Value?.SetWindow(open: false);
                }
            });
        string Tick()
        {
            var previousMeasurement = measurement.Value;
            var current = Volatile.Read(ref launches);
            measurement.Value = current;
            prepNumber++;
            try
            {
                return ConductorBatchLoopTestsStepLedger.Phase(
                    ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver), "acceptance-admission");
            }
            finally
            {
                current.SetWindow(open: false);
                measurement.Value = previousMeasurement;
            }
        }
        Assert.Same(previous, Interlocked.CompareExchange(ref GitCli.LaunchObserver, observer, previous));
        try
        {
            var first = Tick();
            Assert.Contains("prep_merge_conflict_cache=hits:0,misses:1", first);
            AssertLaunches(launches, expectAny: true, "The cold merge-conflict step must exercise real git.");
            Volatile.Write(ref launches, new PrepGitLaunches());
            kernel.RecordGoalPolicyDecision(goal.Id, "Same conflict observed on the next prep");
            var second = Tick();
            Assert.Contains("prep_merge_conflict_cache=hits:1,misses:0", second);
            Assert.Contains("prep_projection_cache=hits:1,misses:0", second);
            if (injectCachedProbe)
            {
                var failure = Assert.Throws<Xunit.Sdk.TrueException>(() => AssertLaunches(launches, expectAny: false,
                    "The cached merge-conflict step must not launch probe git commands."));
                Assert.Contains("counted:worktree", failure.Message);
                return;
            }
            AssertLaunches(launches, expectAny: false, "The cached merge-conflict step must not launch probe git commands.");
            Assert.Equal(1, probes);
            AdditiveConflictGitFixture.Git(fixture.Repository, "commit", "--allow-empty", "-m", "Advance main");
            main = ConductorGitRevisionReader.ReadRequiredCommit(fixture.Repository, "main^{commit}");
            Volatile.Write(ref launches, new PrepGitLaunches());
            Assert.Contains("prep_merge_conflict_cache=hits:0,misses:1", Tick());
            AssertLaunches(launches, expectAny: true, "A moved main must recompute the real merge-conflict step.");
            Assert.Equal(2, probes);
        }
        finally
        {
            Interlocked.CompareExchange(ref GitCli.LaunchObserver, previous, observer);
            driver.PreLandingMergeConflictProbe = previousProbe;
        }
    }

    // LaunchObserver exposes args[0], so command shape alone cannot distinguish key-reader
    // cat-file/rev-parse launches from probe launches. Require the delegate scope as well.
    private static bool IsProbeCommand(string command) => command is
        "worktree" or "-c" or "merge" or "status" or "rev-parse" or "symbolic-ref" or
        "merge-base" or "diff" or "show" or "branch" or "cat-file";

    private static void AssertLaunches(PrepGitLaunches launches, bool expectAny, string reason)
    {
        var (count, commands) = launches.Snapshot();
        Assert.True(expectAny ? count > 0 : count == 0,
            $"{reason} Counted launches: {count}; recorded commands: [{commands}]");
    }

    private sealed class PrepGitLaunches
    {
        private readonly object sync = new();
        private readonly List<(string Command, bool Counted)> commands = [];
        private bool windowOpen;

        public void SetWindow(bool open)
        {
            lock (sync) windowOpen = open;
        }

        public void Record(string command, bool counted)
        {
            lock (sync)
            {
                if (windowOpen) commands.Add((command, counted));
            }
        }

        public (int Count, string Commands) Snapshot()
        {
            lock (sync)
                return (commands.Count(entry => entry.Counted),
                    string.Join(", ", commands.Select(entry => $"{(entry.Counted ? "counted" : "excluded")}:{entry.Command}")));
        }
    }

    private static string Admission(IReadOnlyList<string> lines) => Assert.Single(lines.Where(line =>
        line.StartsWith("PHASE_TIMING ", StringComparison.Ordinal) && line.Contains("phase=acceptance-admission ", StringComparison.Ordinal)));
}
