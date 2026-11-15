using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

public sealed class ConductorDriverTestsRecoveryBaselineEvidenceHold(ITestOutputHelper output)
{
    [Fact]
    public void UnchangedLiveBaseline_SecondTickDerivesNothing()
    {
        using var fixture = new HeldGoalRecoveryFixture();
        fixture.StartBaseline();
        Assert.True(fixture.ChangedFiles > 0);
        Assert.True(fixture.Contexts > 0);
        Assert.True(fixture.Siblings > 0);
        Assert.True(fixture.SourceRequests > 0);
        var first = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Tick().Outcome);
        var before = (fixture.ChangedFiles, fixture.Contexts, fixture.Siblings, fixture.SourceRequests);
        var timeline = fixture.Goal.Timeline.Count;
        var ledger = new ConductorTickStepLedger(() => System.Diagnostics.Process.GetCurrentProcess().TotalProcessorTime);
        ConductorAdvanceOutcome.Held second;
        using (ledger.Activate()) second = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Tick().Outcome);
        output.WriteLine(ledger.FormatWalk(TimeSpan.Zero, TimeSpan.Zero));
        Assert.Equal(before, (fixture.ChangedFiles, fixture.Contexts, fixture.Siblings, fixture.SourceRequests));
        Assert.Equal(first.Reason, second.Reason);
        Assert.Equal(first.Owner, second.Owner);
        Assert.Equal(System.Text.Json.JsonSerializer.Serialize(first.Decision), System.Text.Json.JsonSerializer.Serialize(second.Decision));
        Assert.Equal(timeline, fixture.Goal.Timeline.Count);
        Assert.Equal(0, fixture.Outcomes);
        Assert.Equal(0, fixture.Retries);
    }

    [Fact]
    public void CandidateChanges_SelectionAndChangedFilesAreReobserved()
    {
        using var fixture = new HeldGoalRecoveryFixture();
        fixture.StartBaseline();
        var changed = fixture.ChangedFiles;
        var contexts = fixture.Contexts;
        var siblings = fixture.Siblings;
        fixture.CandidateSha = "def5678";
        fixture.Tick();
        Assert.True(fixture.ChangedFiles > changed);
        Assert.True(fixture.Contexts > contexts);
        Assert.True(fixture.Siblings > siblings);
    }

    [Fact]
    public void EngineChanges_DoNotReuseThePriorAttemptHold()
    {
        using var fixture = new HeldGoalRecoveryFixture();
        fixture.StartBaseline();
        var before = fixture.ChangedFiles;
        fixture.Settings = new() { EnforceStructuralCoverage = !fixture.Settings.EnforceStructuralCoverage };
        fixture.Tick();
        Assert.True(fixture.ChangedFiles > before);
    }

    [Fact]
    public void FindingRoundChanges_DoNotReuseThePriorAttemptHold()
    {
        using var fixture = new HeldGoalRecoveryFixture();
        fixture.StartBaseline();
        var before = fixture.ChangedFiles;
        var reviewer = fixture.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        fixture.Kernel.RetryTask(fixture.Goal.Id, reviewer.Id, "New review round");
        ConductorDriverTests.FailReviewerNeedsWork(fixture.Kernel, fixture.Goal, reviewer, "new finding",
            findings: [ConductorDriverTests.EvidenceFindingWithRequest("new evidence request", id: "new-round", classes: ["ConductorDriverTests"])]);
        fixture.Tick();
        Assert.True(fixture.ChangedFiles > before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompletionBetweenTicks_MatchesCompletionWithoutPriorHold(bool priorHold)
    {
        using var fixture = new HeldGoalRecoveryFixture();
        fixture.StartBaseline();
        if (priorHold) Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Tick().Outcome);
        fixture.Complete(fixture.Baseline, includeBaseline: true);
        var before = fixture.ChangedFiles;
        var result = fixture.Tick();
        Assert.True(fixture.ChangedFiles > before);
        Assert.IsType<ConductorAdvanceOutcome.Executed>(result.Outcome);
        Assert.Equal(1, fixture.Outcomes);
        Assert.Equal(1, fixture.Retries);
        Assert.NotNull(fixture.Goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer)
            .VerificationHistory.Last().MergedReviewFindings!.Single(finding => finding.StableId == "held-baseline").EvidenceOutcome);
        Assert.NotNull(fixture.Coordinator.TryReadLatest(fixture.Goal.Id.Value)!.ReconciledAt);
    }
}
