using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;

public sealed class ImpactReplayClassLevelReportTests
{
    [Fact]
    public void CompleteAndUnavailableAttempts_ReportTotalsShareCoverageAndCpu()
    {
        ImpactReplayAttemptRecord[] attempts =
            [Attempt("selected", 7.25, 9.25), Attempt("abandoned", 0, 9.25),
             Attempt("unresolved", null, 2, "selection unresolved"),
             Attempt("incomplete", null, null, "incomplete TRX duration or class evidence")];
        var measurement = ImpactReplayMeasurement.Create(attempts);
        var summary = measurement.ClassLevel!;
        Assert.Equal(7.25, summary.TotalSecondsSaved);
        Assert.Equal(3.625d, summary.MedianSecondsSaved);
        Assert.Equal(20.5d, summary.NonFloorTestSeconds);
        Assert.Equal(7.25d / 20.5d, summary.ShareOfNonFloorTestSeconds);
        var report = new ImpactReplayReport("override", [], attempts, [], [], [], Measurement: measurement);

        var markdown = ImpactReplayReportWriter.Render(report);

        Assert.Contains("- Balanced-pool seconds saved: 0" + Environment.NewLine +
            "- Class-level seconds saved (total): 7.25" + Environment.NewLine +
            "- Class-level seconds saved (median per attempt): 3.625" + Environment.NewLine +
            "- Share of non-floor test seconds saved at class level: 35.4 %", markdown);
        Assert.Contains("- Class-level coverage: 3 attempts measured, 1 excluded for incomplete TRX, 1 with unresolved selection", markdown);
        Assert.Contains("- Class-level CPU saved: unavailable", markdown);
        Assert.Contains("durations include contention with other lanes", markdown);
        Assert.Contains("upper estimate of wall-time savings", markdown);
        Assert.Contains("better estimate of CPU savings", markdown);
        Assert.True(markdown.IndexOf("Named-lane estimated seconds saved", StringComparison.Ordinal) <
            markdown.IndexOf("Class-level seconds saved (total)", StringComparison.Ordinal));
        Assert.True(markdown.IndexOf("Class-level seconds saved (total)", StringComparison.Ordinal) <
            markdown.IndexOf("Miss count", StringComparison.Ordinal));
        Assert.Contains("| selected | 7.25 | 9.25 | unavailable |", markdown);
        Assert.Contains("| abandoned | 0 | 9.25 | unavailable |", markdown);
        Assert.Contains("| unresolved | unavailable (selection unresolved) | 2 | unavailable |", markdown);
        Assert.Contains("| incomplete | unavailable (incomplete TRX duration or class evidence) | unavailable | unavailable |", markdown);

        using var json = JsonDocument.Parse(JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(7.25, json.RootElement.GetProperty("attempts")[0].GetProperty("classLevelSecondsSaved").GetDouble());
        Assert.Equal(JsonValueKind.Null, json.RootElement.GetProperty("attempts")[3].GetProperty("classLevelSecondsSaved").ValueKind);
        Assert.Equal(7.25, json.RootElement.GetProperty("measurement").GetProperty("classLevel").GetProperty("totalSecondsSaved").GetDouble());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ZeroOrAbsentBaseline_ReportsUnavailableShare(bool incomplete)
    {
        ImpactReplayAttemptRecord[] attempts = [Attempt("zero", incomplete ? null : 0, incomplete ? null : 0)];
        var measurement = ImpactReplayMeasurement.Create(attempts);
        var markdown = ImpactReplayReportWriter.Render(new("override", [], attempts, [], [], [], Measurement: measurement));
        Assert.Null(measurement.ClassLevel!.ShareOfNonFloorTestSeconds);
        Assert.Contains("Share of non-floor test seconds saved at class level: unavailable", markdown);
        Assert.Contains($"Class-level seconds saved (median per attempt): {(incomplete ? "unavailable" : "0")}", markdown);
    }

    [Fact]
    public void OddAndEmptyPopulations_UseOrdinaryMedianAndUnavailable()
    {
        var summary = ImpactReplayClassLevelSummary.Create(
            [Attempt("a", 8, 10), Attempt("b", 0, 10), Attempt("c", 2, 10)]);
        Assert.Equal(2d, summary.MedianSecondsSaved);
        Assert.Null(ImpactReplayClassLevelSummary.Create([]).MedianSecondsSaved);
        Assert.Null(ImpactReplayClassLevelSummary.Create([]).ShareOfNonFloorTestSeconds);
    }

    [Fact]
    public void NoMeasurement_KeepsClassLevelMarkdownOutOfLegacyOutput()
    {
        var markdown = ImpactReplayReportWriter.Render(new("override", [], [Attempt("a", 3, 4)], [], [], []));
        Assert.DoesNotContain("Class-level", markdown);
    }

    private static ImpactReplayAttemptRecord Attempt(string id, double? saved, double? baseline, string? reason = null) =>
        new(id, "goal", DateTimeOffset.UnixEpoch, "main", "branch", "Resolved", null, null,
            [], [], [], [], [], [], 0, [], [], true, [],
            ClassLevelSecondsSaved: saved, ClassLevelNonFloorTestSeconds: baseline,
            ClassLevelUnavailableReason: reason,
            ClassLevelCpuUnavailableReason: "no per-class CPU data read by impact-replay");
}
