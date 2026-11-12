// Parallel-safe: each test owns all Git and filesystem state through its fixture.
public sealed class ImpactReplayUnchangedBaselineTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NoChangedTestsOrFlakesPreservesSelectionMissesAndMarkdown(bool failingBeta)
    {
        using var fixture = new ImpactReplayFixture();
        fixture.AddAttempt("baseline", failingBeta: failingBeta);

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.Equal(new[] { "src/Sample/Alpha.cs" }, attempt.ChangedFiles);
        Assert.Equal(new[] { "AlphaTests" }, attempt.SelectedTestClasses);
        Assert.Equal(new[] { "Alpha lane", "Chaos gate" }, attempt.WouldRunLanes);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        Assert.Equal(0, report.AttemptsGainingChangedTestClasses);
        Assert.Equal(!failingBeta, attempt.MissCheckComplete);
        if (failingBeta)
        {
            var failure = Assert.Single(attempt.FailingClasses);
            Assert.Equal("SampleTests.BetaTests", failure.ClassName);
            Assert.Equal("Beta lane", failure.Lane);
            Assert.True(failure.IsMiss);
            Assert.Null(failure.FlakeEvidence);
        }
        else Assert.Empty(attempt.FailingClasses);

        var expectedLines = new List<string>
        {
            $"Run: {report.Run!.RunId}",
            $"Checkpoint: {report.Run.CheckpointPath}",
            "Consult the checkpoint for completion state. Partial runs are not safety verdicts.",
            "",
            "Floor source: default",
            "Default floor policy: Process spawning*, Isolated process spawning*, Chaos gate, Remainder merge train and ratchet.",
            "Floor lanes: Chaos gate",
            "",
            "Attempts replayed: 1",
            "Unreplayable attempts: 0",
            "Skipped inputs: 0",
            $"Incomplete miss checks: {(failingBeta ? 1 : 0)}",
            "Median share of lanes skipped: 33.3 %",
            "P90 share of lanes skipped: 33.3 %",
            "Total estimated lane-seconds saved: 23.5",
            "",
            "## Selections abandoned, by kind",
            "",
            "None.",
            "",
            "## Misses",
            ""
        };
        expectedLines.AddRange(failingBeta
            ? new[] { "| Attempt | Class | Lane |", "| --- | --- | --- |", "| baseline | SampleTests.BetaTests | Beta lane |" }
            : new[] { "None observed; consult incomplete miss checks before drawing conclusions." });
        expectedLines.AddRange(new[]
        {
            "",
            "## Attempts",
            "",
            "| Attempt | Selection | Lanes ran | Would run | Skipped | Estimated seconds saved |",
            "| --- | --- | --- | --- | --- | --- |",
            "| baseline | Resolved | Alpha lane, Beta lane, Chaos gate | Alpha lane, Chaos gate | Beta lane | 23.5 |",
            "",
            "## Input and evidence diagnostics",
            ""
        });
        if (failingBeta) expectedLines.AddRange(new[]
        {
            "- baseline: Historical coverage unavailable: lane-flakes receipts index adjudications only; retained native TRX has no exhaustive source-bound exact-identity index. Recover authoritative coverage metadata; an exhaustive compatibility scan requires separate operator authorization.",
            "- baseline: Historical evidence incomplete for SampleTests.BetaTests: SampleTests.BetaTests.Works. Recover authoritative exact-identity coverage or authorize a separate diagnostic history scan."
        });
        expectedLines.AddRange(new[]
        {
            "",
            "## Unmapped test classes",
            "",
            "- not-scanned: 0",
            "- no-lane: 0",
            "- ambiguous: 0",
            "",
            "No unmapped test classes were found."
        });
        Assert.Equal("# Impact replay\n\n" + string.Join(Environment.NewLine, expectedLines) + Environment.NewLine, markdown);
    }
}
