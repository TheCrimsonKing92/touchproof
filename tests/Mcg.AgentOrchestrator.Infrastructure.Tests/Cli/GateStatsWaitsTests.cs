using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private evidence roots, no processes, environment changes or shared stores.
public sealed class GateStatsWaitsTests : CliTaskQueryTestSupport
{
    [Fact]
    public async Task Coverage_ZeroAndMissing_PreservesUnknownAndRouteApplicability()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        f.Write(Row("zero", M("ready-to-admitted", 0, 0), M("admitted-to-execution", 0, 0)),
            Row("missing"), Row("remote") with { Route = "remote" });
        var report = f.Report();
        Assert.Equal(3, report.Occurrences);
        Assert.Equal(1, report.LaneWaitSamples);
        Assert.Equal(2, report.LaneWaitMissing);
        Assert.Equal(0, report.SummedLaneWaitSeconds);
        var ready = Assert.Single(report.Stages, s => s.StageName == "ready-to-admitted");
        Assert.Equal((3, 1, 2, (double?)0), (ready.Applicable, ready.Samples, ready.Missing, ready.TotalSeconds));
        var remote = Assert.Single(report.Stages, s => s.StageName == "remote-build-wait");
        Assert.Equal((1, 0, 1, 2), (remote.Applicable, remote.Samples, remote.Missing, remote.Inapplicable));
        Assert.Null(remote.TotalSeconds);
        Assert.Null(Assert.Single(report.GateWall).GateWallSeconds);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public async Task InvalidNumbers_InvalidateOnlyTheirStage(double value)
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        f.Write(Row("lane", M("ready-to-admitted", 0, 2), M("admitted-to-execution", 2, 3)));
        var json = JsonNode.Parse(File.ReadAllText(f.Attempt.MetadataPath))!;
        json["laneWaits"]![0]!["measurements"]![1]!["seconds"] = double.IsFinite(value)
            ? JsonValue.Create(value) : JsonValue.Create(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        File.WriteAllText(f.Attempt.MetadataPath, json.ToJsonString());
        var typed = JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(json.ToJsonString(), AcceptanceLaneWaitArtifactFixture.Json);
        Assert.Equal(ConductorParallelAcceptanceAttemptOutcome.Running, Assert.IsType<ConductorParallelAcceptanceAttempt>(typed).Outcome);
        var report = f.Report();
        Assert.Equal(1, Assert.Single(report.Stages, s => s.StageName == "ready-to-admitted").Samples);
        Assert.Equal(1, Assert.Single(report.Stages, s => s.StageName == "admitted-to-execution").Missing);
        Assert.Equal(2, Assert.Single(Assert.Single(report.Rows).Observations).Seconds);
        Assert.Null(report.SummedLaneWaitSeconds);
    }

    [Theory]
    [InlineData("attemptId", "another-attempt")]
    [InlineData("invocationId", "another:lane:0")]
    [InlineData("invocationId", "12345678-0-20261103000000000-00000000000000000000000000000000:lane:1")]
    public async Task MismatchedIdentity_ContributesUnknown(string field, string value)
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        f.Write(Row("lane", M("ready-to-admitted", 0, 3), M("admitted-to-execution", 3, 3)));
        var json = JsonNode.Parse(File.ReadAllText(f.Attempt.MetadataPath))!;
        json["laneWaits"]![0]![field] = value;
        File.WriteAllText(f.Attempt.MetadataPath, json.ToJsonString());
        var row = Assert.Single(f.Report().Rows);
        Assert.Empty(row.Observations);
        Assert.Null(row.SummedLaneWaitSeconds);
        Assert.Equal(1, Assert.Single(row.Stages, s => s.StageName == "ready-to-admitted").Missing);
    }

    [Theory]
    [InlineData(2, 5, 10)] // Nested component.
    [InlineData(8, 13, 13)] // Overlapping component.
    [InlineData(12, 15, 13)] // Disjoint component.
    public async Task ComparableIntervals_UseUnionInsteadOfStageSum(double start, double end, double total)
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        f.Write(Row("lane", M("ready-to-admitted", 0, 8), M("admitted-to-execution", 8, 10),
            M("shard-permit", start, end)));
        Assert.Equal(total, Assert.Single(f.Report().Rows).SummedLaneWaitSeconds);
        Assert.Equal(end - start, Assert.Single(f.Report().Stages, s => s.StageName == "shard-permit").TotalSeconds);
    }

    [Fact]
    public async Task DifferentClockDomains_KeepStages_LeaveCombinedTotalUnknown()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        f.Write(Row("lane", M("ready-to-admitted", 0, 4),
            M("admitted-to-execution", 4, 5) with { ClockDomain = "another-process" }));
        var row = Assert.Single(f.Report().Rows);
        Assert.Equal(2, row.Observations.Count);
        Assert.Null(row.SummedLaneWaitSeconds);
    }

    [Fact]
    public async Task IncompleteAndDuplicateObservations_RemainMissing()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        f.Write(Row("lane", M("ready-to-admitted", 0, 4) with { EndSeconds = null },
            M("admitted-to-execution", 4, 5), M("admitted-to-execution", 4, 5)));
        var report = f.Report();
        Assert.Null(report.SummedLaneWaitSeconds);
        Assert.All(report.Stages.Where(s => s.Applicable > 0), s => Assert.Equal(1, s.Missing));
    }

    [Fact]
    public async Task RemoteBuildWait_RemainsSeparateFromLocalWaitAndGateWall()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        f.Write(Row("remote", M("ready-to-admitted", 0, 3),
            M("admitted-to-execution", 3, 4) with { Boundary = "submission-start" },
            new("remote-build-wait", "existing-remote-phase", "remote-status-duration", 100)) with { Route = "remote" });
        File.WriteAllText(Path.Combine(f.DirectoryPath, GateLaneTimeLedger.FileName), JsonSerializer.Serialize(new
        { gateAttemptId = AcceptanceLaneWaitArtifactFixture.Id, totalMs = 9000 }) + "\n");
        var report = f.Report();
        Assert.Equal(100, Assert.Single(report.Stages, s => s.StageName == "remote-build-wait").TotalSeconds);
        Assert.Equal(9, Assert.Single(report.GateWall).GateWallSeconds);
        // A remote route must also directly observe its claim boundary.
        Assert.Null(report.SummedLaneWaitSeconds);
    }

    [Fact]
    public async Task LegacyTrxAndReceiptOnlyAttempts_ApplyLastAfterUnionDiscovery()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        File.WriteAllText(f.Prefix + ".legacy-run-1.trx", "<TestRun><Results /></TestRun>");
        var old = Assert.Single(f.Report().Rows);
        Assert.Equal("unknown", old.Route);
        Assert.Equal(1, old.Retry);
        Assert.Null(old.SummedLaneWaitSeconds);
        var newer = AcceptanceLaneWaitArtifactFixture.Id.Replace("20261103", "20261104", StringComparison.Ordinal);
        var row = Row("cancelled") with { AttemptId = newer };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(f.Prefix)!, newer + ".attempt.json"),
            JsonSerializer.Serialize(new { attemptId = newer, laneWaits = new[] { row } }, AcceptanceLaneWaitArtifactFixture.Json));
        var report = f.Report(last: 1);
        Assert.Equal(newer, Assert.Single(report.Rows).Attempt);
        Assert.Single(report.GateWall);
    }

    [Fact]
    public async Task RerunsAndFallbackRoutes_RetainSeparateCoverageAndTopRanking()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        var first = Row("lane", M("ready-to-admitted", 0, 2), M("admitted-to-execution", 2, 2));
        var retry = first with { OccurrenceId = "retry", Retry = 1, InvocationId = null };
        f.Write(first, retry, Row("lane") with { Route = "remote", OccurrenceId = "fallback" });
        var report = f.Report(top: 1);
        Assert.Single(report.Rows);
        Assert.Equal(3, report.Occurrences);
        Assert.Equal(2, report.LaneWaitSamples);
        Assert.Equal(4, report.SummedLaneWaitSeconds);
    }

    [Theory]
    [InlineData("--top", "0")]
    [InlineData("--last", "-1")]
    [InlineData("--top", "bad")]
    [InlineData("--lane", "lane")]
    [InlineData("--top")]
    [InlineData("--json", "--json")]
    [InlineData("--last", "1", "--last", "2")]
    public async Task Arguments_InvalidWaitOptions_ThrowUsage(params string[] args)
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        Assert.Throws<ArgumentException>(() => CliGateStatsQueryCommand.Execute(["gate-stats", "waits", .. args],
            OrchestratorWorkspace.ForDirectory(f.Root)));
    }

    [Fact]
    public async Task Cli_UsesReadOnlyRouteAndMatchingJsonTextValues()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        f.Write(Row("lane", M("ready-to-admitted", 0, 3), M("admitted-to-execution", 3, 4)));
        var before = Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(["gate-stats", "waits"]));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(["gate-stats", "waits"]));
        var workspace = OrchestratorWorkspace.ForDirectory(f.Root);
        var text = CaptureConsole(() => CliGateStatsQueryCommand.Execute(["gate-stats", "waits"], workspace));
        var json = CaptureConsole(() => CliGateStatsQueryCommand.Execute(["gate-stats", "waits", "--json"], workspace));
        using var document = JsonDocument.Parse(json);
        Assert.Equal(4, document.RootElement.GetProperty("summed_lane_wait_seconds").GetDouble());
        Assert.Contains("summed_lane_wait_seconds=4", text, StringComparison.Ordinal);
        Assert.Contains("gate_wall=", text, StringComparison.Ordinal);
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(f.Root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Contains("gate-stats waits", CliCommandHelp.GateStatsUsage, StringComparison.Ordinal);
    }

    [Fact]
    public void EmptyEvidence_DoesNotCreateAStore()
    {
        var root = Path.Combine(Path.GetTempPath(), "absent-waits-" + Guid.NewGuid().ToString("N"));
        Assert.Empty(GateStatsReport.Waits(root, 10, 20).Rows);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public async Task MalformedOptionalExtension_DoesNotInvalidateTypedVerdictReaders()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        var verification = new AcceptanceVerificationResult(true, false, 0, null);
        var json = JsonSerializer.SerializeToNode(verification, AcceptanceLaneWaitArtifactFixture.Json)!;
        json["laneWaits"] = "malformed optional extension";
        Assert.True(JsonSerializer.Deserialize<AcceptanceVerificationResult>(json.ToJsonString(), AcceptanceLaneWaitArtifactFixture.Json)!.Passed);
        var summary = AcceptanceVerificationSummary.FromVerification(verification, [], [], "commit", "main", [], [], null);
        json = JsonSerializer.SerializeToNode(summary, AcceptanceLaneWaitArtifactFixture.Json)!;
        json["laneWaits"] = new JsonArray(JsonValue.Create(42));
        Assert.True(JsonSerializer.Deserialize<AcceptanceVerificationSummary>(json.ToJsonString(), AcceptanceLaneWaitArtifactFixture.Json)!.Passed);
        json = JsonSerializer.SerializeToNode(f.Attempt, AcceptanceLaneWaitArtifactFixture.Json)!;
        json["laneWaits"] = "invalid";
        Assert.Equal(f.Attempt.Outcome, JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(json.ToJsonString(), AcceptanceLaneWaitArtifactFixture.Json)!.Outcome);
    }

    [Fact]
    public async Task OverflowingRetrySuffix_IsSkippedWithoutBecomingRetryZero()
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        File.WriteAllText(f.Prefix + ".lane-run-999999999999999999999.trx", "<TestRun><Results /></TestRun>");
        var report = f.Report();
        Assert.Empty(report.Rows);
        Assert.Equal(1, report.Skipped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedOccurrenceIdentity_KeepsUnknownCoverage(bool route)
    {
        await using var f = new AcceptanceLaneWaitArtifactFixture();
        f.Write(Row("lane", M("ready-to-admitted", 0, 3), M("admitted-to-execution", 3, 3)));
        var json = JsonNode.Parse(File.ReadAllText(f.Attempt.MetadataPath))!;
        json["laneWaits"]![0]![route ? "route" : "invocationId"] = route ? JsonValue.Create("invalid") : new JsonObject();
        File.WriteAllText(f.Attempt.MetadataPath, json.ToJsonString());
        var row = Assert.Single(f.Report().Rows);
        Assert.Empty(row.Observations);
        Assert.Null(row.SummedLaneWaitSeconds);
        Assert.Equal(1, Assert.Single(row.Stages, s => s.StageName == "ready-to-admitted").Missing);
    }

    private static AcceptanceLaneWaitOccurrence Row(string name, params AcceptanceLaneWaitMeasurement[] samples) =>
        new(AcceptanceLaneWaitArtifactFixture.Id, name, name, "local", 0, null,
            samples.Select(m => m.Stage).Distinct().ToArray(), samples);
    private static AcceptanceLaneWaitMeasurement M(string stage, double start, double end) => new(stage,
        stage switch { "ready-to-admitted" => "all-local-grants", "admitted-to-execution" => "executor-invoked", _ => "permit-acquired" },
        "local-monotonic", end - start, "process", start, end);
}
