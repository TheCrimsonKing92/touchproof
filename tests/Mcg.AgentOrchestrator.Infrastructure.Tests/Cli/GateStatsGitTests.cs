using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: reports read only fixture ledgers and logs in an independently owned root.
public sealed class GateStatsGitTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "git-stats", Guid.NewGuid().ToString("N"));
    private string ReceiptPath => Path.Combine(root, "gate-lane-receipts.jsonl");
    private string LogDirectory => Path.Combine(root, "logs");

    [Theory]
    [InlineData(40, 150, "close")]
    [InlineData(50, 200, "close")]
    [InlineData(50.1, 100, "step-2")]
    [InlineData(10, 200.1, "step-2")]
    public void MeasuredWindows_ApplyStrictAggregateThresholds(double gateMs, double tickMs, string verdict)
    {
        WriteReceipts(Lane("a", gateMs), Lane("b", gateMs));
        WriteTicks(tickMs);
        var lines = Report("--last", "2");
        Assert.Equal($"verdict={verdict} gates=2 requested=2 gate_threshold_pct=5 tick_threshold_pct=20", lines[^1]);
        Assert.Equal(2, lines.Count(line => line.StartsWith("gate=")));
        var tick = Assert.Single(lines, line => line.StartsWith("tick="));
        Assert.Contains("launches=2", tick);
        Assert.Contains("call_sites=Example.Read:2", tick);
        Assert.Contains("git_timed=2 subcommands=status:2", tick);
        foreach (var line in lines.Where(line => line.StartsWith("gate=") || line.StartsWith("tick=")))
        {
            Assert.Contains("share_pct=", line);
            Assert.Contains("share_capped_pct=", line);
            Assert.Contains("git_wall_ms=", line);
        }
        Assert.Contains(lines, line => line.Contains("parallel launches can exceed elapsed time"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TooFewMeasuredGates_ReportInsufficientEvenWithHighTickShare(bool legacy)
    {
        WriteReceipts(Lane("a", 90), legacy ? "{\"wall_ms\":1000}" : "{broken");
        WriteTicks(900);
        Assert.Equal("verdict=insufficient-data gates=1 requested=2 gate_threshold_pct=5 tick_threshold_pct=20",
            Report("--last", "2")[^1]);
    }

    [Fact]
    public void EnoughGatesWithoutTicks_KeepCloseDecisionInsufficient()
    {
        WriteReceipts(Lane("a", 1));
        Assert.StartsWith("verdict=insufficient-data", Report("--last", "1")[^1]);
    }

    [Fact]
    public void ParallelDurations_PrintRawAndCappedShares()
    {
        WriteReceipts(Lane("a", 1500));
        WriteTicks(2000);
        var lines = Report("--last", "1");
        Assert.Contains("share_pct=150 share_capped_pct=100", Assert.Single(lines, line => line.StartsWith("gate=")));
        Assert.Contains("share_pct=200 share_capped_pct=100", Assert.Single(lines, line => line.StartsWith("tick=")));
        Assert.StartsWith("verdict=step-2", lines[^1]);
    }

    [Fact]
    public void RepeatedLaneRows_DeduplicateAndAggregateOneAttempt()
    {
        var a = Lane("a", 80, "first");
        WriteReceipts(a, a, Lane("a", 0, "second"), Lane("older", 9999, "first", "2026-01-01T00:00:00Z"));
        WriteTicks(0);
        var lines = Report("--last", "1");
        var gate = Assert.Single(lines, line => line.StartsWith("gate="));
        Assert.Contains("gate=a launches=4 git_wall_ms=80 wall_ms=2000 share_pct=4 ", gate);
        Assert.Contains("call_sites=Example.Read:4", gate);
        Assert.StartsWith("verdict=close", lines[^1]);
    }

    [Theory]
    [InlineData("first")]
    [InlineData("cached-only")]
    public void CachedLaneResults_LeaveSourceGateAccountingUnchanged(string reusedLane)
    {
        RecordLane("first", 2, 80, 2000, DateTimeOffset.UnixEpoch);
        RecordLane(reusedLane, 2, 80, 1, DateTimeOffset.UnixEpoch.AddSeconds(1), reused: true);
        WriteTicks(0);

        var lines = Report("--last", "1");
        var gate = Assert.Single(lines, line => line.StartsWith("gate="));
        Assert.Contains("gate=a launches=2 git_wall_ms=80 wall_ms=2000 share_pct=4 ", gate);
        Assert.Contains("call_sites=Example.Read:2", gate);
        Assert.Contains(lines, line => line.Contains("skipped=1"));
        Assert.StartsWith("verdict=close", lines[^1]);
    }

    [Fact]
    public void ZeroLaunchSidecar_KeepsLaneWallTimeInGateShare()
    {
        RecordLane("first", 2, 80, 1000, DateTimeOffset.UnixEpoch);
        RecordLane("idle", 0, 0, 1000, DateTimeOffset.UnixEpoch.AddSeconds(1));
        WriteTicks(0);

        var lines = Report("--last", "1");
        var gate = Assert.Single(lines, line => line.StartsWith("gate="));
        Assert.Contains("gate=a launches=2 git_wall_ms=80 wall_ms=2000 share_pct=4 ", gate);
        Assert.StartsWith("verdict=close", lines[^1]);
    }

    [Fact]
    public void RotatedTickLogs_AreReadAndMalformedEventsAreSkipped()
    {
        WriteReceipts(Lane("a", 0));
        WriteTicks(250);
        var path = Path.Combine(LogDirectory, "conduct-events.log");
        File.Move(path, Path.Combine(LogDirectory, "conduct-events-20261112.log"));
        File.WriteAllLines(path, ["{broken", JsonSerializer.Serialize(new
        {
            timestamp = "2026-11-12T12:00:00Z", eventKind = "tick-git-census", detail = "TICK_GIT_CENSUS tick=oops"
        })]);
        var lines = Report("--last", "1");
        Assert.Single(lines, line => line.StartsWith("tick="));
        Assert.Contains(lines, line => line.Contains("skipped=2"));
        Assert.StartsWith("verdict=step-2", lines[^1]);
    }

    [Fact]
    public void MissingFiles_DispatchReadOnlyAndUseDefaultTenGates()
    {
        string[] args = ["gate-stats", "git"];
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var workspace = OrchestratorWorkspace.ForDirectory(root);
        var output = CaptureConsole(() => CliGateStatsQueryCommand.Execute(args, workspace));
        Assert.Contains("verdict=insufficient-data gates=0 requested=10", output);
        Assert.False(Directory.Exists(root));
        Assert.Contains("gate-stats git [--last <n>] [--top <k>]", CliCommandHelp.GateStatsUsage);
    }

    [Theory]
    [InlineData("--last")]
    [InlineData("--last", "0")]
    [InlineData("--top", "-1")]
    [InlineData("--last", "NaN")]
    [InlineData("--last", "1", "--last", "2")]
    [InlineData("--json")]
    public void InvalidFlags_ThrowSharedUsage(params string[] flags) =>
        Assert.Equal(CliCommandHelp.GateStatsUsage, Assert.Throws<ArgumentException>(() => Report(flags)).Message);

    private string[] Report(params string[] flags)
    {
        using var output = new StringWriter();
        CliGateStatsGitCommand.Execute(["gate-stats", "git", .. flags], ReceiptPath, LogDirectory, output);
        return output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Lane(string attempt, double ms, string lane = "first", string at = "2026-11-12T12:00:00Z") =>
        JsonSerializer.Serialize(new
        {
            attempt_id = attempt, lane, run_ordinal = 1, recorded_at = at, wall_ms = 1000,
            slot_git_launches = 2, slot_git_wall_ms = ms,
            slot_git_call_sites = new Dictionary<string, long> { ["Example.Read"] = 2 }
        });

    private void WriteReceipts(params string[] lines)
    {
        Directory.CreateDirectory(root);
        File.WriteAllLines(ReceiptPath, lines);
    }

    private void RecordLane(string lane, int launches, double gitMs, double wallMs,
        DateTimeOffset at, bool reused = false)
    {
        Directory.CreateDirectory(root);
        var trx = Path.Combine(root, lane + ".trx");
        File.WriteAllText(trx, """
            <TestRun><Results><UnitTestResult testName="Example.Tests.Method" outcome="Passed" duration="00:00:05" /></Results></TestRun>
            """);
        var sidecar = new Dictionary<string, object>
        {
            ["test_name"] = "Example.Tests.Method", ["wait_ms"] = 0, ["held_ms"] = wallMs,
            ["slot_count"] = 2, ["process_id"] = Environment.ProcessId,
            ["git_launches"] = launches,
            ["git_callers"] = launches > 0
                ? new Dictionary<string, long> { ["src:Example.Read<-Parent.Method|status"] = launches, ["other"] = 0 }
                : new Dictionary<string, long> { ["other"] = 0 }
        };
        if (launches > 0) sidecar["git_wall_ms"] = gitMs;
        File.WriteAllText(LaneSlotTimingReader.PathFor(trx), JsonSerializer.Serialize(sidecar));
        var result = new AcceptanceCheckResult(lane, true, 0, null,
            TestResultPaths: [trx], TestResultAttemptId: "a", TestResultRunOrdinal: 1,
            TestResultIsExplicitCrossAttemptReuse: reused);
        GateLaneReceiptLedger.Record(root, "goal", lane, result, TimeSpan.FromMilliseconds(wallMs), at);
        File.Copy(GateLaneReceiptLedger.ResolveStorePath(root), ReceiptPath, overwrite: true);
        Assert.True(File.Exists(ReceiptPath));
    }

    private void WriteTicks(double wall)
    {
        using var census = new GitLaunchWindowCensus();
        using var window = census.OpenWindow("tick");
        for (var i = 0; i < 2; i++)
        {
            var sink = census.BeginTiming("status", "Example.Read");
            census.RecordLaunch("status", "Example.Read");
            sink!(new("status", 0, wall / 2, null));
        }
        new ConductEventLogWriter(Path.Combine(LogDirectory, "conduct-events.log"))
            .Append("tick-git-census", null, GitLaunchWindowCensus.FormatTick(1, 1000, window.Close()));
    }

    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, true); }
}
