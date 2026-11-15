using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Isolated kernel and injected CPU; no shared processes or machine observations.
[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsWalkPhaseIsolation(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void AdvanceOnce_AdmissionRecordAndCleanup_PreservePhaseCpu()
    {
        var (kernel, goal) = SimpleGoal();
        PassVerification(kernel, goal, goal.Tasks.Single());
        var cpu = TimeSpan.Zero;
        var factsRead = 0;
        var recorded = false;
        var records = 0;
        var cleanups = 0;
        var context = new PreReviewEvidenceContext("candidate", [], null, "fixture", true, false);
        ConductorDriver driver = null!;
        driver = MakeDriver(
            getFacts: current =>
            {
                factsRead++;
                // Shared delegates must not acquire a walk frame via AdvanceOnce.
                Assert.Equal(new[] { "src/Changed.cs" }, driver.ReadAcceptanceCandidate(current).ScopePaths);
                Assert.Same(context, driver.GetPreReviewEvidenceContext(current));
                return new GoalLifecycleFacts(IsMerged: true, IsRecorded: recorded);
            },
            getLandingFileScopes: _ => { cpu += TimeSpan.FromMilliseconds(7); return ["src/Changed.cs"]; },
            getPreReviewEvidenceContext: _ => { cpu += TimeSpan.FromMilliseconds(11); return context; },
            record: _ => { records++; recorded = true; cpu += TimeSpan.FromMilliseconds(13); },
            cleanup: _ =>
            {
                cleanups++;
                cpu += TimeSpan.FromMilliseconds(17);
                return new GoalWorktreeRemoveResult("Workspace cleaned up.", null, [], null);
            });
        var ledger = new ConductorTickStepLedger(() => cpu);
        using var active = ledger.Activate();
        var before = ledger.FormatWalk(TimeSpan.Zero, TimeSpan.Zero);

        void RecordAndCleanup()
        {
            recorded = false;
            var record = Assert.IsType<ConductorAdvanceOutcome.Executed>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
            Assert.Equal(GoalLifecycleState.Merged, record.FromState);
            var cleanup = Assert.IsType<ConductorAdvanceOutcome.Executed>(
                driver.AdvanceOnce(goal, ConductorAutonomyPolicy.Permissive).Outcome);
            Assert.Equal(GoalLifecycleState.Recorded, cleanup.FromState);
        }

        ConductorTickStepLedger.Measure("prep-approval-hold", RecordAndCleanup);

        Assert.Equal(1, records);
        Assert.Equal(1, cleanups);
        Assert.True(factsRead >= 2, "Both admission advances must read lifecycle facts.");
        Assert.Equal(TimeSpan.FromMilliseconds(factsRead * 18 + 30), cpu);
        Assert.Equal(before, ledger.FormatWalk(TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal($"prep-approval-hold:{(long)cpu.TotalMilliseconds}:1",
            ledger.FormatSteps(["prep-approval-hold"]));

        // The same driver retains detailed attribution under a real walk frame.
        var admissionFactsRead = factsRead;
        ConductorTickStepLedger.Measure("walk-advance", RecordAndCleanup);
        var walkFactsRead = factsRead - admissionFactsRead;
        Assert.Equal(2, records);
        Assert.Equal(2, cleanups);
        Assert.Equal("walk-hold-entry-gates:30:2,walk-hold-lifecycle-reconcile:0:2," +
            "walk-hold-lifecycle-facts:0:2", ledger.FormatSteps([
                "walk-hold-entry-gates", "walk-hold-lifecycle-reconcile", "walk-hold-lifecycle-facts"]));
        Assert.Equal($"walk-changed-files:{walkFactsRead * 7}:{walkFactsRead}," +
            $"walk-candidate-context:{walkFactsRead * 11}:{walkFactsRead}",
            ledger.FormatSteps(["walk-changed-files", "walk-candidate-context"]));

        var walkWindowCpu = cpu;
        var afterWalk = ledger.FormatWalk(TimeSpan.Zero, walkWindowCpu);
        ConductorTickStepLedger.Measure("prep-approval-hold", RecordAndCleanup);
        Assert.Equal(3, records);
        Assert.Equal(3, cleanups);
        Assert.Equal(afterWalk, ledger.FormatWalk(TimeSpan.Zero, walkWindowCpu));
    }

    [Fact]
    public void AdvanceOnce_AdmissionRunningHold_LeavesWalkUnchanged()
    {
        using var fixture = new HeldGoalWorkerFixture(0);
        var cpu = TimeSpan.Zero;
        var reads = 0;
        var driver = MakeDriver(getFacts: _ =>
        {
            reads++;
            cpu += TimeSpan.FromMilliseconds(7);
            return new GoalLifecycleFacts(WorkspaceExists: true);
        });
        var ledger = new ConductorTickStepLedger(() => cpu);
        using var active = ledger.Activate();
        var before = ledger.FormatWalk(TimeSpan.Zero, TimeSpan.Zero);

        ConductorTickStepLedger.Measure("prep-approval-hold", () =>
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
                driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);
            Assert.Equal(GoalLifecycleState.Running, held.State);
        });

        Assert.True(reads > 0);
        Assert.Equal(before, ledger.FormatWalk(TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal($"prep-approval-hold:{reads * 7}:1", ledger.FormatSteps(["prep-approval-hold"]));
    }

    [Fact]
    public void AdvanceOnce_AdmissionRecoveryHold_LeavesWalkUnchanged()
    {
        using var fixture = new HeldGoalRecoveryFixture();
        fixture.StartBaseline();
        var cpu = TimeSpan.Zero;
        var reads = 0;
        fixture.Driver.RecoveryCandidateRevision = _ =>
        {
            reads++;
            cpu += TimeSpan.FromMilliseconds(5);
            return fixture.CandidateSha;
        };
        var ledger = new ConductorTickStepLedger(() => cpu);
        using var active = ledger.Activate();
        var before = ledger.FormatWalk(TimeSpan.Zero, TimeSpan.Zero);

        ConductorTickStepLedger.Measure("prep-approval-hold", () =>
            Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Tick().Outcome));

        Assert.True(reads > 0, "Recovery must inspect the running baseline candidate.");
        Assert.Equal(before, ledger.FormatWalk(TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal($"prep-approval-hold:{reads * 5}:1", ledger.FormatSteps(["prep-approval-hold"]));
    }

    [Theory]
    [InlineData("prep-retry-priority")]
    [InlineData("attempt-reconcile")]
    [InlineData("reconcile-remediation")]
    public void SharedDelegates_OutsideWalk_PreservePhaseCpu(string phase)
    {
        var (_, goal) = SimpleGoal();
        var cpu = TimeSpan.Zero;
        var filesRead = 0;
        var contextsRead = 0;
        var context = new PreReviewEvidenceContext("candidate", [], null, "fixture", true, false);
        var driver = MakeDriver(
            getLandingFileScopes: _ =>
            {
                filesRead++;
                cpu += TimeSpan.FromMilliseconds(7);
                return ["src/Changed.cs"];
            },
            getPreReviewEvidenceContext: _ =>
            {
                contextsRead++;
                cpu += TimeSpan.FromMilliseconds(11);
                return context;
            });
        var ledger = new ConductorTickStepLedger(() => cpu);
        using var active = ledger.Activate();
        void ReadSharedDelegates()
        {
            Assert.Equal(new[] { "src/Changed.cs" }, driver.ReadAcceptanceCandidate(goal).ScopePaths);
            Assert.Same(context, driver.GetPreReviewEvidenceContext(goal));
        }

        var before = ledger.FormatWalk(TimeSpan.Zero, TimeSpan.Zero);
        ConductorTickStepLedger.Measure(phase, ReadSharedDelegates);

        Assert.Equal(1, filesRead);
        Assert.Equal(1, contextsRead);
        Assert.Equal(before, ledger.FormatWalk(TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal($"{phase}:18:1", ledger.FormatSteps([phase]));

        // The same delegates still produce exclusive sub-steps inside the walk.
        ConductorTickStepLedger.Measure("walk-advance", ReadSharedDelegates);
        Assert.Equal(2, filesRead);
        Assert.Equal(2, contextsRead);
        Assert.Equal("walk-advance:0:1,walk-changed-files:7:1,walk-candidate-context:11:1",
            ledger.FormatSteps(["walk-advance", "walk-changed-files", "walk-candidate-context"]));

        // Closing the walk frame restores phase ownership on subsequent calls.
        var afterWalk = ledger.FormatWalk(TimeSpan.Zero, TimeSpan.FromMilliseconds(18));
        ConductorTickStepLedger.Measure(phase, ReadSharedDelegates);
        Assert.Equal(3, filesRead);
        Assert.Equal(3, contextsRead);
        Assert.Equal(afterWalk, ledger.FormatWalk(TimeSpan.Zero, TimeSpan.FromMilliseconds(18)));
        Assert.Equal($"{phase}:36:2", ledger.FormatSteps([phase]));
    }
}
