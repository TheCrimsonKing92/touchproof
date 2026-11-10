using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: classified attempt records, aggregation and rendering are entirely in memory.
public sealed class ImpactReplayMeasurementTests
{
    [Theory]
    [InlineData("Balanced 8", false, "lane-flakes", 0, 1)]
    [InlineData("Balanced 8", false, "historical-flakes", 0, 1)]
    [InlineData("Balanced 8", false, null, 1, 0)]
    [InlineData("Balanced 8", true, null, 0, 0)]
    [InlineData("Balanced 8", true, "lane-flakes", 0, 0)]
    [InlineData("Balanced 8", null, null, 0, 0)]
    [InlineData("Balanced 8", null, "lane-flakes", 0, 0)]
    [InlineData(null, false, null, 0, 0)]
    [InlineData(null, false, "lane-flakes", 0, 0)]
    [InlineData(null, null, null, 0, 0)]
    public void ClassifiedFailure_AggregatesOnlyEligibleCategory(
        string? lane, bool? wouldRun, string? evidence, int misses, int flakes)
    {
        var failure = new ImpactReplayFailingClass("FailureTests", lane, wouldRun, evidence);

        var measurement = ImpactReplayMeasurement.Create(20, [Attempt("one", failure)]);

        Assert.Equal(misses == 1, failure.IsMiss);
        Assert.Equal(flakes == 1, failure.IsFlakyFailureInSkippedLane);
        Assert.Equal(misses, measurement.MissCount);
        Assert.Equal(flakes, measurement.FlakeExcludedCount);
    }

    [Fact]
    public void MixedRows_CountCategoriesIndependentlyAndPreserveDetails()
    {
        var report = Report(Attempt("mixed",
            new("BothTests", "Balanced 8", false),
            new("BothTests", "Balanced 8", false, "lane-flakes"),
            new("FlakeOnlyTests", "Balanced 8", false, "lane-flakes"),
            new("SelectedTests", "Balanced 8", true)));
        var measurement = report.Measurement!;

        var markdown = ImpactReplayReportWriter.Render(report);
        using var json = Serialize(report);
        var summary = json.RootElement.GetProperty("measurement");
        Assert.Equal(1, summary.GetProperty("missCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("flakeExcludedCount").GetInt32());
        Assert.Equal(1, measurement.MissCount);
        Assert.Equal(2, measurement.FlakeExcludedCount);
        Assert.Contains("Miss count (true misses): 1", markdown);
        Assert.Contains("Flake-excluded count (known flaky skipped failures): 2", markdown);
        Assert.Contains("## Misses", markdown);
        Assert.Contains("| mixed | BothTests | Balanced 8 |", markdown);
        Assert.Contains("## Flaky failures in skipped lanes", markdown);
        Assert.Contains("| mixed | BothTests | Balanced 8 | lane-flakes |", markdown);
        Assert.Contains("| mixed | FlakeOnlyTests | Balanced 8 | lane-flakes |", markdown);
        var rows = json.RootElement.GetProperty("attempts")[0].GetProperty("failingClasses");
        Assert.Equal(4, rows.GetArrayLength());
        Assert.True(rows[0].GetProperty("isMiss").GetBoolean());
        Assert.False(rows[1].GetProperty("isMiss").GetBoolean());
        Assert.True(rows[1].GetProperty("isFlakyFailureInSkippedLane").GetBoolean());
        Assert.Contains(summary.GetProperty("interpretation").GetString()!, markdown);
        Assert.Contains("not an additive partition", measurement.Interpretation);
    }

    [Fact]
    public void DuplicateRows_CountOrdinalClassOncePerAttemptInEachCategory()
    {
        ImpactReplayFailingClass[] failures =
        [
            new("MissTests", "Balanced 8", false),
            new("MissTests", "Balanced 8", false),
            new("MissTests", "Balanced 9", false),
            new("misstests", "Balanced 8", false),
            new("FlakeTests", "Balanced 8", false, "lane-flakes"),
            new("FlakeTests", "Balanced 8", false, "lane-flakes"),
            new("FlakeTests", "Balanced 9", false, "historical-flakes")
        ];

        var single = ImpactReplayMeasurement.Create(20, [Attempt("first", failures)]);
        var repeated = ImpactReplayMeasurement.Create(20,
            [Attempt("first", failures), Attempt("second", failures)]);

        Assert.Equal(2, single.MissCount);
        Assert.Equal(1, single.FlakeExcludedCount);
        Assert.Equal(4, repeated.MissCount);
        Assert.Equal(2, repeated.FlakeExcludedCount);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void IncompleteEvidence_RemainsVisibleWithoutErasingKnownMiss(
        bool includeMiss, int expectedMisses)
    {
        ImpactReplayFailingClass[] unknowns =
        [
            new("NoLaneTests", null, false),
            new("UnknownSelectionTests", "Balanced 8", null)
        ];
        var attempt = Attempt("incomplete", includeMiss
            ? [.. unknowns, new("KnownMissTests", "Balanced 8", false)] : unknowns) with
        {
            MissCheckComplete = false,
            EvidenceWarnings = ["would-run evidence unavailable"],
            UnresolvedFailures = [new("NoLaneTests", "origin.trx", "lane unresolved")]
        };
        var report = Report(attempt);
        var markdown = ImpactReplayReportWriter.Render(report);
        using var json = Serialize(report);

        Assert.Equal(expectedMisses, report.Measurement!.MissCount);
        Assert.Equal(0, report.Measurement.FlakeExcludedCount);
        Assert.Equal(0, report.Measurement.CompleteMissCheckCount);
        Assert.Contains("Incomplete miss checks: 1", markdown);
        Assert.Contains("Complete miss checks: 0", markdown);
        Assert.Contains("would-run evidence unavailable", markdown);
        Assert.Contains("## Unresolved failing classes", markdown);
        Assert.Contains("NoLaneTests; TRX: origin.trx; lane unresolved", markdown);
        Assert.Contains("Unknown or unresolved evidence remains", markdown);
        Assert.Contains("Zero true misses does not establish selection safety", markdown);
        if (!includeMiss)
            Assert.Contains("None observed; consult incomplete miss checks", markdown);
        var entry = json.RootElement.GetProperty("attempts")[0];
        Assert.False(entry.GetProperty("missCheckComplete").GetBoolean());
        Assert.Equal("would-run evidence unavailable", entry.GetProperty("evidenceWarnings")[0].GetString());
        Assert.Equal("NoLaneTests", entry.GetProperty("unresolvedFailures")[0].GetProperty("identity").GetString());
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("failingClasses")[0].GetProperty("lane").ValueKind);
        Assert.Equal(JsonValueKind.Null, entry.GetProperty("failingClasses")[1].GetProperty("wouldRun").ValueKind);
        Assert.Equal(expectedMisses, json.RootElement.GetProperty("measurement").GetProperty("missCount").GetInt32());
    }

    [Fact]
    public void RetainedThreeCaseProjection_ExcludesOnlySkippedFlake()
    {
        // Operator-supplied projection in typed/goal/timeline.json (HumanInputReceived,
        // ecdc5f606f3642b08f102aa0829bdf6c); this does not execute a historical replay.
        var report = Report(
            Attempt("6592b97b-0-20261109190930842-96ff2aaf14b14c33b43a69abaa94b747",
                new ImpactReplayFailingClass("BuildLaunchQosSweepTests", "Balanced 8", false, "lane-flakes")),
            Attempt("b5d7127e-0-20261109174420747-df14fd353e384ac0b56f470e286f0ec9",
                new("ChildOutputDrainAdoptionSaturationTests", "Process spawning process-local", true),
                new("WorkerBuildScopeToolCacheDigestTests", "Balanced 3", true)),
            Attempt("a9ec3eb1-0-20261109163927634-ef757456a3ed4ec48404eabe247f9234"));

        Assert.Equal(0, report.Measurement!.MissCount);
        Assert.Equal(1, report.Measurement.FlakeExcludedCount);
        Assert.Equal(3, report.Measurement.CompleteMissCheckCount);
        var failure = Assert.Single(report.Attempts[0].FailingClasses);
        Assert.False(failure.IsMiss);
        Assert.True(failure.IsFlakyFailureInSkippedLane);
        var markdown = ImpactReplayReportWriter.Render(report);
        using var json = Serialize(report);
        Assert.Equal(0, json.RootElement.GetProperty("measurement").GetProperty("missCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("measurement").GetProperty("flakeExcludedCount").GetInt32());
        Assert.Contains("Miss count (true misses): 0", markdown);
        Assert.Contains("Flake-excluded count (known flaky skipped failures): 1", markdown);
        Assert.Contains("| BuildLaunchQosSweepTests | Balanced 8 | lane-flakes |", markdown);
        var interpretation = json.RootElement.GetProperty("measurement").GetProperty("interpretation").GetString()!;
        Assert.Contains(interpretation, markdown);
        Assert.Contains("Historical summaries may include skipped flaky failures", interpretation);
        Assert.Contains("not directly comparable to corrected true-miss totals without reconciliation", interpretation);
    }

    [Fact]
    public void EmptyPopulation_ReportsZeroCategoryCounts()
    {
        var measurement = ImpactReplayMeasurement.Create(20, []);
        Assert.Equal(0, measurement.MissCount);
        Assert.Equal(0, measurement.FlakeExcludedCount);
        Assert.Equal(0, measurement.CompleteMissCheckCount);
    }

    private static ImpactReplayAttemptRecord Attempt(string id,
        params ImpactReplayFailingClass[] failures) =>
        new(id, "goal", DateTimeOffset.UnixEpoch, "main", "branch", "Resolved", null, null,
            [], [], [], [], [], [], 0, [], failures, true, []);

    private static ImpactReplayReport Report(params ImpactReplayAttemptRecord[] attempts) =>
        new("override", [], attempts, [], [], [], Measurement: ImpactReplayMeasurement.Create(20, attempts));

    private static JsonDocument Serialize(ImpactReplayReport report) =>
        JsonDocument.Parse(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}
