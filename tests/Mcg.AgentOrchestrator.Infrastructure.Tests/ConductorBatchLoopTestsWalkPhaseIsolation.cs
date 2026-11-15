using Mcg.AgentOrchestrator.App.Orchestration;

// Isolated kernel and injected CPU; no shared processes or machine observations.
[Collection(TestCollections.DotnetBuildSlots)]
public sealed class ConductorBatchLoopTestsWalkPhaseIsolation(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
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
