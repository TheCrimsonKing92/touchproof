using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each case owns its Git repository, receipts and output files.
public sealed class ImpactReplayUnmappedTestClassNegativeControlTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public void MappedFixturesKeepLegacyFiguresAndMarkdownPrefix(int sourceCount)
    {
        using var fixture = new ImpactReplayFixture(changedSourceCount: sourceCount);
        fixture.AddAttempt("control");

        var (report, markdown) = fixture.Run();

        var attempt = Assert.Single(report.Attempts);
        Assert.Equal("Resolved", attempt.SelectionOutcome);
        Assert.Null(attempt.DegradationKind);
        Assert.Equal(new[] { "Alpha lane", "Beta lane", "Chaos gate" }, attempt.RanLanes);
        Assert.Equal(new[] { "Alpha lane", "Chaos gate" }, attempt.WouldRunLanes);
        Assert.Equal(new[] { "Beta lane" }, attempt.SkippedLanes);
        Assert.Equal(1d / 3, attempt.ShareOfLanesSkipped);
        Assert.Equal(23.5, attempt.EstimatedSerialSecondsSaved);
        Assert.Empty(attempt.FailingClasses);
        Assert.True(attempt.MissCheckComplete);
        Assert.Equal(0, report.AttemptsGainingChangedTestClasses);

        var legacyLines = new[]
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
            "Incomplete miss checks: 0",
            "Median share of lanes skipped: 33.3 %",
            "P90 share of lanes skipped: 33.3 %",
            "Total estimated lane-seconds saved: 23.5",
            "",
            "## Selections abandoned, by kind",
            "",
            "None.",
            "",
            "## Misses",
            "",
            "None observed; consult incomplete miss checks before drawing conclusions.",
            "",
            "## Attempts",
            "",
            "| Attempt | Selection | Lanes ran | Would run | Skipped | Estimated seconds saved |",
            "| --- | --- | --- | --- | --- | --- |",
            "| control | Resolved | Alpha lane, Beta lane, Chaos gate | Alpha lane, Chaos gate | Beta lane | 23.5 |",
            "",
            "## Input and evidence diagnostics",
            ""
        };
        var legacy = "# Impact replay\n\n" + string.Join(Environment.NewLine, legacyLines) + Environment.NewLine;
        var heading = Environment.NewLine + "## Unmapped test classes";
        var sectionStart = markdown.IndexOf(heading, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, "Unmapped test classes section was not written.");
        Assert.Equal(legacy, markdown[..sectionStart]);
        var unmappedSection = Environment.NewLine + string.Join(Environment.NewLine, new[]
        {
            "## Unmapped test classes", "", "- not-scanned: 0", "- no-lane: 0", "- ambiguous: 0", "",
            "No unmapped test classes were found."
        }) + Environment.NewLine;
        Assert.Equal(unmappedSection, markdown[sectionStart..]);

        var diagnostics = Assert.IsType<ImpactReplayUnmappedTestClasses>(report.UnmappedTestClasses);
        Assert.Equal(3, diagnostics.CountsByReason.Count);
        Assert.All(diagnostics.CountsByReason, item => Assert.Equal(0, item.Value));
        Assert.Empty(diagnostics.TopClasses);
        Assert.Empty(diagnostics.Occurrences);
        var json = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.OutputDirectory, "impact-replay.json")))!.AsObject();
        Assert.Equal(new[] { "attempts", "attemptsGainingChangedTestClasses", "cleanupErrors", "floorLanes", "floorSource",
            "run", "skippedInputs", "unmappedTestClasses", "unreplayableAttempts" }, json.Select(item => item.Key).Order(StringComparer.Ordinal));
        Assert.Equal(report.Run!.RunId, json["run"]!["runId"]!.GetValue<string>());
        Assert.Equal(0, json["unmappedTestClasses"]!["countsByReason"]!["not-scanned"]!.GetValue<int>());
        Assert.Equal(0, json["unmappedTestClasses"]!["countsByReason"]!["no-lane"]!.GetValue<int>());
        Assert.Equal(0, json["unmappedTestClasses"]!["countsByReason"]!["ambiguous"]!.GetValue<int>());
        Assert.Empty(json["unmappedTestClasses"]!["topClasses"]!.AsArray());
        Assert.Empty(json["unmappedTestClasses"]!["occurrences"]!.AsArray());
    }
}
