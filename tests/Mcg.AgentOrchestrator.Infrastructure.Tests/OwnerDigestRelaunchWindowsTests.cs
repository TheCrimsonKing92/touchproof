using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;

// Parallel-safe: fixture owns its SQLite stores and every log file.
public sealed class OwnerDigestRelaunchWindowsTests
{
    private static readonly DateTimeOffset Start = OwnerDigestTestFixture.Start;

    [Fact]
    public async Task RotatedSequenceAndPartialSequenceReportExactPhasesAndJsonParity()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var directory = fixture.Workspace.LogDirectory;
        Directory.CreateDirectory(directory);
        Write(directory, "conduct-events-20260924173000.log",
            Event(1000, "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED"),
            Event(187000, "loop-stop", "LOOP_STOP reason=self-relaunch-handoff"));
        Write(directory, "conduct-events.log",
            Event(231000, "loop-first-tick", "LOOP_FIRST_TICK", 193800),
            Event(308000, "loop-handoff", "ACTIVATION_ADOPTED"),
            Event(400000, "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED"),
            Event(410000, "loop-stop", "LOOP_STOP reason=self-relaunch-handoff"),
            Event(420000, "loop-first-tick", "LOOP_FIRST_TICK", 411000));

        var report = CliOwnerDigestRelaunch.Read(fixture.Workspace, Start, Start.AddDays(1));
        Assert.Equal(2, report.Windows.Count);
        var complete = report.Windows[0];
        Assert.Equal("complete", complete.Status);
        Assert.Empty(complete.Issues);
        Assert.Equal(186000d, complete.DrainMs);
        Assert.Equal(44000d, complete.RestartMs);
        Assert.Equal(77000d, complete.ProbationMs);
        Assert.Equal(307000d, complete.TotalMs);
        var incomplete = report.Windows[1];
        Assert.Equal("incomplete", incomplete.Status);
        Assert.Contains("missing adoption", incomplete.Issues);
        Assert.Equal(10000d, incomplete.DrainMs);
        Assert.Equal(10000d, incomplete.RestartMs);
        Assert.Null(incomplete.ProbationMs);
        Assert.Null(incomplete.TotalMs);
        Assert.Equal(1, report.CompleteCount);
        Assert.Equal(1, report.IncompleteCount);
        Assert.Equal(new RelaunchMetric(2, 196000, 98000), report.Drain);
        Assert.Equal(new RelaunchMetric(2, 54000, 27000), report.Restart);
        Assert.Equal(new RelaunchMetric(1, 77000, 77000), report.Probation);
        Assert.Equal(new RelaunchMetric(1, 307000, 307000), report.Combined);

        var text = Run(fixture, "--relaunch");
        Assert.Contains("complete | 186000 | 44000 | 77000 | 307000", text);
        Assert.Contains("incomplete | 10000 | 10000 | n/a | n/a", text);
        Assert.Contains("drain: count=2 total_ms=196000 median_ms=98000", text);
        using var json = JsonDocument.Parse(Run(fixture, "--relaunch", "--rounds", "--json"));
        var node = json.RootElement.GetProperty("relaunch");
        Assert.True(json.RootElement.TryGetProperty("rounds", out _));
        Assert.Equal(186000, node.GetProperty("windows")[0].GetProperty("drainMs").GetDouble());
        Assert.Equal(44000, node.GetProperty("windows")[0].GetProperty("restartMs").GetDouble());
        Assert.Equal(77000, node.GetProperty("windows")[0].GetProperty("probationMs").GetDouble());
        Assert.Equal(307000, node.GetProperty("windows")[0].GetProperty("totalMs").GetDouble());
        Assert.Equal(JsonValueKind.Null, node.GetProperty("windows")[1].GetProperty("probationMs").ValueKind);
        Assert.Equal(98000, node.GetProperty("drain").GetProperty("medianMs").GetDouble());
        Assert.Equal(1, node.GetProperty("completeCount").GetInt32());
        Assert.Equal(1, node.GetProperty("incompleteCount").GetInt32());
        using var baseline = JsonDocument.Parse(Run(fixture, "--json"));
        Assert.False(baseline.RootElement.TryGetProperty("relaunch", out _));
    }

    [Fact]
    public async Task WindowFiltersBeforeMatchingAndCliPercentilesUseAllRotations()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var directory = fixture.Workspace.LogDirectory;
        Directory.CreateDirectory(directory);
        Write(directory, "conduct-events.log", Event(-1, "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED"),
            Event(1, "loop-stop", "LOOP_STOP reason=self-relaunch-handoff"),
            Event(2, "loop-first-tick", "LOOP_FIRST_TICK"), Event(3, "loop-handoff", "ACTIVATION_ADOPTED"),
            Event(100, "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED"),
            Event(110, "loop-stop", "LOOP_STOP reason=self-relaunch-handoff"),
            Event(120, "loop-first-tick", "LOOP_FIRST_TICK"),
            Event(86400000, "loop-handoff", "ACTIVATION_ADOPTED"));
        Write(directory, "cli-timing-20260924010000.jsonl", Timing(0, "fresh", 3700), Timing(1, "fresh", 4000),
            Timing(2, "fresh", 4100), Timing(-1, "built", 999999));
        Write(directory, "cli-timing.jsonl", Timing(3, "fresh", 4500), Timing(4, "fresh", 9000),
            Timing(5, "built", 60000), Timing(6, "built", 70000), Timing(7, null, null),
            Timing(86400000, "built", 999999), "malformed", "{\"timestamp\":\"invalid\"}");

        var report = CliOwnerDigestRelaunch.Read(fixture.Workspace, Start, Start.AddDays(1));
        var window = Assert.Single(report.Windows);
        Assert.Equal(Start.AddMilliseconds(100), window.Scheduled);
        Assert.Equal("incomplete", window.Status);
        Assert.Null(window.Adopted);
        Assert.Equal(new CliLatencyPath("fresh", 5, 4100, 9000), report.CliLatency.Paths.Single(p => p.Path == "fresh"));
        Assert.Equal(new CliLatencyPath("built", 2, 60000, 70000), report.CliLatency.Paths.Single(p => p.Path == "built"));
        Assert.Equal(new CliLatencyPath("fast", 0, null, null), report.CliLatency.Paths.Single(p => p.Path == "fast"));
        Assert.Equal(2, report.CliLatency.BuiltCount);
        Assert.Equal(1, report.CliLatency.UnclassifiedCount);
        Assert.Equal(2, report.CliLatency.SkippedRecords);
        var text = Run(fixture, "--relaunch");
        Assert.Contains("fresh | 5 | 4100 | 9000", text);
        Assert.Contains("built | 2 | 60000 | 70000", text);
        Assert.Contains("built=2 unclassified=1 skipped=2", text);
        using var json = JsonDocument.Parse(Run(fixture, "--relaunch", "--json"));
        var latency = json.RootElement.GetProperty("relaunch").GetProperty("cliLatency");
        var fresh = latency.GetProperty("paths").EnumerateArray().Single(p => p.GetProperty("path").GetString() == "fresh");
        Assert.Equal(5, fresh.GetProperty("count").GetInt32());
        Assert.Equal(4100, fresh.GetProperty("p50Ms").GetDouble());
        Assert.Equal(9000, fresh.GetProperty("p95Ms").GetDouble());
        var built = latency.GetProperty("paths").EnumerateArray().Single(p => p.GetProperty("path").GetString() == "built");
        Assert.Equal(2, built.GetProperty("count").GetInt32());
        Assert.Equal(60000, built.GetProperty("p50Ms").GetDouble());
        Assert.Equal(70000, built.GetProperty("p95Ms").GetDouble());
        Assert.Equal(2, latency.GetProperty("builtCount").GetInt32());
    }

    [Fact]
    public async Task RepeatsStaleTicksAndFailedAttemptsFollowChronologicalRules()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var directory = fixture.Workspace.LogDirectory;
        Directory.CreateDirectory(directory);
        // File order is deliberately opposite timestamp order.
        Write(directory, "conduct-events-a.log",
            Event(70000, "loop-handoff", "ACTIVATION_ADOPTED"),
            Event(30000, "loop-first-tick", "LOOP_FIRST_TICK", 0),
            Event(40000, "loop-first-tick", "LOOP_FIRST_TICK", 25000),
            Event(50000, "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED"),
            Event(60000, "loop-stop", "LOOP_STOP reason=self-relaunch-handoff"));
        Write(directory, "conduct-events-z.log",
            Event(10000, "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED"),
            Event(11000, "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED"),
            Event(15000, "loop-stop", "LOOP_STOP reason=other"),
            Event(20000, "loop-stop", "LOOP_STOP reason=self-relaunch-handoff"),
            Event(80000, "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED"),
            Event(81000, "loop-handoff", "ACTIVATION_REVERTED"),
            Event(90000, "loop-relaunch", "LOOP_RELAUNCH_SCHEDULED"),
            Event(91000, "loop-handoff", "LOOP_HANDOFF_FAILED"));
        var report = CliOwnerDigestRelaunch.Read(fixture.Workspace, Start, Start.AddDays(1));
        Assert.Equal(4, report.Windows.Count);
        Assert.Equal(Start.AddMilliseconds(10000), report.Windows[0].Scheduled);
        Assert.Equal(Start.AddMilliseconds(40000), report.Windows[0].FirstTick);
        Assert.Contains("superseded by schedule", report.Windows[0].Issues);
        Assert.Equal(Start.AddMilliseconds(70000), report.Windows[1].Adopted);
        Assert.Contains("missing first tick", report.Windows[1].Issues);
        Assert.Contains("ACTIVATION_REVERTED", report.Windows[2].Issues);
        Assert.Contains("LOOP_HANDOFF_FAILED", report.Windows[3].Issues);
        Assert.Equal(4, report.IncompleteCount);
    }

    [Fact]
    public async Task EmptyAndMalformedLogsKeepNullAggregates()
    {
        using var fixture = await OwnerDigestTestFixture.CreateAsync();
        var empty = CliOwnerDigestRelaunch.Read(fixture.Workspace, Start, Start.AddDays(1));
        Assert.Empty(empty.Windows);
        Assert.Equal(new RelaunchMetric(0, null, null), empty.Drain);
        Assert.All(empty.CliLatency.Paths, p => Assert.Equal(0, p.Count));
        Directory.CreateDirectory(fixture.Workspace.LogDirectory);
        Write(fixture.Workspace.LogDirectory, "conduct-events.log", "[]", "not-json", "{\"timestamp\":3}");
        Assert.Equal(3, CliOwnerDigestRelaunch.Read(fixture.Workspace, Start, Start.AddDays(1)).SkippedRecords);
    }

    private static string Run(OwnerDigestTestFixture fixture, params string[] flags)
    {
        using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var args = new[] { "owner-digest", "--since", Start.ToString("O"), "--until", Start.AddDays(1).ToString("O") }.Concat(flags).ToArray();
        Assert.Equal(0, CliOwnerDigestCommand.Run(args, fixture.Workspace, fixture.Clock, writer));
        return writer.ToString();
    }

    private static string Event(int milliseconds, string kind, string detail, int? processStarted = null) =>
        JsonSerializer.Serialize(new { timestamp = Start.AddMilliseconds(milliseconds), eventKind = kind, detail,
            process_started = processStarted is null ? (DateTimeOffset?)null : Start.AddMilliseconds(processStarted.Value) });
    private static string Timing(int milliseconds, string? path, double? elapsed) =>
        JsonSerializer.Serialize(new { timestamp = Start.AddMilliseconds(milliseconds), launcher_path = path, launcher_elapsed_ms = elapsed });
    private static void Write(string directory, string file, params string[] lines) => File.WriteAllLines(Path.Combine(directory, file), lines);
}
