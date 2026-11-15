using System.Globalization;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: explicit TextWriter capture and uniquely owned fixture copies.
public sealed class GateStatsReplayTests : CliTaskQueryTestSupport
{
    private static string FixtureDirectory => Path.Combine(VerifiedRepositoryRoot.Find(), "tests",
        "Mcg.AgentOrchestrator.Infrastructure.Tests", "TestData", "Fixtures", "LaneWallReplay");

    [Fact]
    public void Replay_ThreeLaneRecordedFixture_PrintsActualPredictedAndImprovedPacking()
    {
        var manifest = Path.Combine(FixtureDirectory, "gate.json");
        var result = new AcceptanceBalancedLaneReplay().Read(manifest);
        Assert.Equal("serial-three-lanes", result.GateId);
        Assert.Equal([180d, 120d, 120d], result.Lanes.Select(lane => lane.ActualWallSeconds));
        Assert.Equal([180d, 120d, 120d], result.Lanes.Select(lane => lane.PredictedWallSeconds));
        Assert.Equal(120, result.NewMaxPredictedWallSeconds);
        Assert.True(result.NewMaxPredictedWallSeconds < result.Lanes.Max(lane => lane.ActualWallSeconds));
        using var output = new StringWriter();
        CliGateStatsReplayCommand.Execute(["gate-stats", "replay", "--gate", manifest], output);
        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
        Assert.Equal(4, lines.Length);
        Assert.Equal("gate=serial-three-lanes\tlane=Balanced 1\tactual_wall_seconds=180\tpredicted_wall_seconds=180", lines[0]);
        Assert.Equal("gate=serial-three-lanes\tlane=Balanced 2\tactual_wall_seconds=120\tpredicted_wall_seconds=120", lines[1]);
        Assert.Equal("gate=serial-three-lanes\tlane=Balanced 3\tactual_wall_seconds=120\tpredicted_wall_seconds=120", lines[2]);
        Assert.Equal("gate=serial-three-lanes\tnew_max_predicted_wall_seconds=120", lines[3]);
        string[] args = ["gate-stats", "replay", "--gate", manifest];
        CliCommandHelp.ThrowIfInvalidFlags(args);
        // Replay uses explicit artifacts and must not require the live workspace ledger.
        Assert.Equal(output.ToString(), CaptureConsole(() => CliGateStatsQueryCommand.Execute(args, null!)));
        using var multiple = new StringWriter();
        CliGateStatsReplayCommand.Execute(["gate-stats", "replay", "--gate", manifest, "--gate", manifest], multiple);
        Assert.Equal(output.ToString() + output.ToString(), multiple.ToString());
    }

    [Fact]
    public void Replay_DurationDriftWithinTolerance_MatchesUndriftedResult()
    {
        var root = Path.Combine(Path.GetTempPath(), "wall-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var path in Directory.GetFiles(FixtureDirectory)) File.Copy(path, Path.Combine(root, Path.GetFileName(path)));
            var manifest = Path.Combine(root, "gate.json");
            var baseline = new AcceptanceBalancedLaneReplay().Read(manifest);
            using var baselineOutput = new StringWriter();
            CliGateStatsReplayCommand.Execute(["gate-stats", "replay", "--gate", manifest], baselineOutput);

            var trx = Path.Combine(root, "lane1.trx");
            var document = XDocument.Load(trx);
            var row = document.Descendants().First(node => node.Name.LocalName == "UnitTestResult");
            var start = DateTimeOffset.Parse((string)row.Attribute("startTime")!, CultureInfo.InvariantCulture);
            var end = DateTimeOffset.Parse((string)row.Attribute("endTime")!, CultureInfo.InvariantCulture);
            row.SetAttributeValue("duration", (end - start + TimeSpan.FromMilliseconds(15)).ToString("c", CultureInfo.InvariantCulture));
            document.Save(trx);
            Assert.Equal(TimeSpan.FromMilliseconds(15),
                TimeSpan.Parse((string)row.Attribute("duration")!, CultureInfo.InvariantCulture) - (end - start));

            var result = new AcceptanceBalancedLaneReplay().Read(manifest);
            Assert.Equal(baseline.GateId, result.GateId);
            Assert.Equal(baseline.Lanes.ToArray(), result.Lanes.ToArray());
            Assert.Equal(baseline.NewMaxPredictedWallSeconds, result.NewMaxPredictedWallSeconds);
            using var output = new StringWriter();
            CliGateStatsReplayCommand.Execute(["gate-stats", "replay", "--gate", manifest], output);
            Assert.Equal(baselineOutput.ToString(), output.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("missing-trx", "lane1.trx")]
    [InlineData("missing-sidecar", "slot timing")]
    [InlineData("missing-receipt", "gate-lane-receipts.jsonl")]
    [InlineData("missing-time", "start/end/duration")]
    [InlineData("duration-mismatch", "start/end/duration")]
    [InlineData("duplicate-class", "two lanes")]
    [InlineData("missing-lane", "Receipt lanes")]
    [InlineData("unattributed-row", "unattributed slot row")]
    public void Replay_IncompleteOrAmbiguousArtifacts_FailsWithGateAndCause(string mutation, string expected)
    {
        var root = Path.Combine(Path.GetTempPath(), "wall-replay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var path in Directory.GetFiles(FixtureDirectory)) File.Copy(path, Path.Combine(root, Path.GetFileName(path)));
            var trx = Path.Combine(root, "lane1.trx");
            var receipts = Path.Combine(root, "gate-lane-receipts.jsonl");
            if (mutation == "missing-trx") File.Delete(trx);
            else if (mutation == "missing-sidecar") File.Delete(LaneSlotTimingReader.PathFor(trx));
            else if (mutation == "missing-receipt") File.Delete(receipts);
            else if (mutation == "missing-time") File.WriteAllText(trx, File.ReadAllText(trx).Replace("startTime=", "missingTime="));
            else if (mutation == "duration-mismatch")
            {
                var document = XDocument.Load(trx);
                var row = document.Descendants().First(node => node.Name.LocalName == "UnitTestResult");
                var start = DateTimeOffset.Parse((string)row.Attribute("startTime")!, CultureInfo.InvariantCulture);
                var end = DateTimeOffset.Parse((string)row.Attribute("endTime")!, CultureInfo.InvariantCulture);
                row.SetAttributeValue("duration", (end - start + TimeSpan.FromSeconds(1)).ToString("c", CultureInfo.InvariantCulture));
                document.Save(trx);
            }
            else if (mutation == "duplicate-class")
            {
                var second = Path.Combine(root, "lane2.trx");
                File.WriteAllText(second, File.ReadAllText(second).Replace("LargeA", "LongA"));
                var timing = LaneSlotTimingReader.PathFor(second);
                File.WriteAllText(timing, File.ReadAllText(timing).Replace("LargeA", "LongA"));
            }
            else if (mutation == "missing-lane") File.WriteAllLines(receipts, File.ReadAllLines(receipts).Skip(1));
            else File.AppendAllLines(LaneSlotTimingReader.PathFor(trx),
                ["""{"test_name":"Fixture.Missing.Test","wait_ms":0,"held_ms":1,"slot_count":1,"process_id":1}"""]);
            using var output = new StringWriter();
            var error = Assert.Throws<InvalidDataException>(() => CliGateStatsReplayCommand.Execute(
                ["gate-stats", "replay", "--gate", Path.Combine(root, "gate.json")], output));
            Assert.Contains("serial-three-lanes", error.Message);
            Assert.Contains(expected, error.Message);
            Assert.Equal("", output.ToString());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Replay_InvalidArguments_ReportsUsage()
    {
        using var output = new StringWriter();
        Assert.Throws<ArgumentException>(() => CliGateStatsReplayCommand.Execute(["gate-stats", "replay"], output));
        Assert.Throws<ArgumentException>(() => CliGateStatsReplayCommand.Execute(["gate-stats", "replay", "--unknown", "x"], output));
    }
}
