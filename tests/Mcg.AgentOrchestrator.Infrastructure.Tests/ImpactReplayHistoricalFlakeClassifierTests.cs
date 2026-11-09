using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns tiny TRX files; no subprocesses or repository reads.
public sealed class ImpactReplayHistoricalFlakeClassifierTests
{
    [Theory]
    [InlineData(1, 0, true)]
    [InlineData(0, 0, false)]
    [InlineData(1, 1, false)]
    [InlineData(0, 1, false)]
    public void OnlyPositiveFlakesWithZeroHardFailuresMoveMissIntoFlakyTable(int flakes, int hard, bool flaky)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var lane = WriteLane(fixture.Root, "Sample.GuardTests.Works");
        var row = new GateStatsReport.FlakeRow("Sample.GuardTests.Works", 45, 44 - hard, flakes, hard, flakes / 45d);
        var result = ImpactReplayHistoricalFlakeClassifier.Classify([new("Sample.GuardTests", "Skipped lane", false)],
            [lane], [new("Sample.GuardTests", null)], History(row));

        var failure = Assert.Single(result);
        Assert.Equal(flaky, failure.IsFlakyFailureInSkippedLane);
        Assert.Equal(!flaky, failure.IsMiss);
        var markdown = Render(result, lane);
        if (flaky)
        {
            Assert.Equal("gate-stats-history: Sample.GuardTests.Works attempts=45 flake=1 hard=0", failure.FlakeEvidence);
            Assert.Contains("## Flaky failures in skipped lanes", markdown);
            Assert.Contains("| historical | Sample.GuardTests | Skipped lane | gate-stats-history:", markdown);
            Assert.DoesNotContain("Sample.GuardTests", markdown[..markdown.IndexOf("## Flaky failures", StringComparison.Ordinal)]);
        }
        else
        {
            Assert.Null(failure.FlakeEvidence);
            Assert.Contains("| historical | Sample.GuardTests | Skipped lane |", markdown);
            Assert.DoesNotContain("## Flaky failures in skipped lanes", markdown);
        }
    }

    [Theory]
    [InlineData("Sample.GuardTests")]
    [InlineData("sample.GuardTests.Works")]
    [InlineData("Sample.GuardTests.Other")]
    [InlineData("")]
    public void MissingExactTestHistoryNeverFallsBackToClassOrCaseInsensitiveMatch(string historyKey)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var lane = WriteLane(fixture.Root, "Sample.GuardTests.Works");
        var result = ImpactReplayHistoricalFlakeClassifier.Classify([new("Sample.GuardTests", "Skipped lane", false)],
            [lane], [new("Sample.GuardTests", null)], History(new(historyKey, 45, 44, 1, 0, 1d / 45)));
        Assert.True(Assert.Single(result).IsMiss);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EveryFailingTestInClassNeedsSupportingHistory(bool hardSibling)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var lane = WriteLane(fixture.Root, "Sample.GuardTests.Works", "Sample.GuardTests.Other");
        var history = History(new("Sample.GuardTests.Works", 45, 44, 1, 0, 1d / 45));
        if (hardSibling) history.Add("Sample.GuardTests.Other", new("Sample.GuardTests.Other", 1, 0, 0, 1, 0));
        var result = ImpactReplayHistoricalFlakeClassifier.Classify([new("Sample.GuardTests", "Skipped lane", false)],
            [lane], [new("Sample.GuardTests", null)], history);
        Assert.True(Assert.Single(result).IsMiss);
    }

    [Theory]
    [InlineData("lane-flakes", false)]
    [InlineData("rerun", false)]
    [InlineData(null, true)]
    public void ExistingEvidenceAndSelectedFailuresArePreserved(string? existingEvidence, bool wouldRun)
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var original = new ImpactReplayFailingClass("Sample.GuardTests", "Skipped lane", wouldRun, existingEvidence);
        var result = ImpactReplayHistoricalFlakeClassifier.Classify([original],
            [WriteLane(fixture.Root, "Sample.GuardTests.Works")], [new("Sample.GuardTests", null)],
            History(new("Sample.GuardTests.Works", 45, 44, 1, 0, 1d / 45)));
        Assert.Same(original, Assert.Single(result));
    }

    [Fact]
    public void UnreadableOrAbsentFailureReceiptCannotEstablishHistoryForClass()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var original = new ImpactReplayFailingClass("Sample.GuardTests", "Skipped lane", false);
        var history = History(new("Sample.GuardTests.Works", 45, 44, 1, 0, 1d / 45));
        foreach (var lane in new[] { new ImpactReplayLaneOutcome("Skipped lane", "Failed", []),
                     new("Skipped lane", "Failed", [Path.Combine(fixture.Root, "missing.trx")]),
                     WriteLane(fixture.Root, "Sample.OtherTests.Works") })
            Assert.True(Assert.Single(ImpactReplayHistoricalFlakeClassifier.Classify([original], [lane],
                [new("Sample.GuardTests", null)], history)).IsMiss);
    }

    [Fact]
    public void HistorySnapshotLoadsAllAvailableAttemptsAndDoesNotReportFilenameSkipsAsFaults()
    {
        using var fixture = new ImpactReplaySelectionFixture();
        var goal = Path.Combine(fixture.Root, "acceptance-gate-attempts", "goal");
        Directory.CreateDirectory(goal);
        const string attempt = "goal-20260101000000000-00000000000000000000000000000001";
        var first = WriteLane(goal, "Sample.GuardTests.Works").TrxPaths.Single();
        File.Move(first, Path.Combine(goal, attempt + ".lane.trx"));
        WriteTrx(Path.Combine(goal, attempt + ".lane-run-1.trx"), "Passed", ["Sample.GuardTests.Works"]);
        File.WriteAllText(Path.Combine(goal, "not-an-attempt.trx"), "<TestRun />");
        var diagnostics = new List<ImpactReplayDiagnostic>();

        var history = ImpactReplayHistoricalFlakeClassifier.Load(fixture.Root, diagnostics);

        var row = Assert.Single(history).Value;
        Assert.Equal("Sample.GuardTests.Works", row.Test);
        Assert.Equal(1, row.Attempts);
        Assert.Equal(1, row.Flake);
        Assert.Equal(0, row.Hard);
        Assert.Empty(diagnostics);
    }

    private static Dictionary<string, GateStatsReport.FlakeRow> History(GateStatsReport.FlakeRow row) =>
        new(StringComparer.Ordinal) { [row.Test] = row };

    private static ImpactReplayLaneOutcome WriteLane(string root, params string[] tests)
    {
        var path = Path.Combine(root, "failed.trx");
        WriteTrx(path, "Failed", tests);
        return new("Skipped lane", "Failed", [path]);
    }

    private static void WriteTrx(string path, string outcome, string[] tests)
    {
        new XDocument(new XElement("TestRun", new XElement("Results", tests.Select(test =>
            new XElement("UnitTestResult", new XAttribute("testName", test), new XAttribute("outcome", outcome),
                new XAttribute("duration", "00:00:00.001")))))).Save(path);
    }

    private static string Render(IReadOnlyList<ImpactReplayFailingClass> failures, ImpactReplayLaneOutcome lane)
    {
        var attempt = new ImpactReplayAttemptRecord("historical", "goal", DateTimeOffset.UnixEpoch, "main", "branch",
            "Resolved", null, null, [], [], [], [lane.Lane], [], [lane.Lane], 0, [lane], failures, true, []);
        return ImpactReplayReportWriter.Render(new("override", [], [attempt], [], [], []));
    }
}
