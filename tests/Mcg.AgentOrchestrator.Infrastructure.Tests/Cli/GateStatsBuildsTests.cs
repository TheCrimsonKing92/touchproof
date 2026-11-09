using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: hand-written ledgers in an independently owned temporary directory.
public sealed class GateStatsBuildsTests : CliTaskQueryTestSupport, IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "build-stats", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Builds_ReportsOriginTotalsAndRepeatCpuExcludingCacheHits()
    {
        Write(
            Row("worker-check", "a", 1000, "none", "2026-11-09T10:00:00Z"),
            Row("gate", "a", 3000, "miss", "2026-11-09T11:00:00Z"),
            Row("train", "a", 200, "hit", "2026-11-09T12:00:00Z"),
            Row("pre-review-evidence", "b", 500, "none", "2026-11-09T13:00:00Z"));
        Assert.Equal(new[]
        {
            "origin=gate\tbuilds=1\tcpu_ms=3000\twall_ms=10\tcache_hits=0\tcpu_unavailable=0",
            "origin=worker-check\tbuilds=1\tcpu_ms=1000\twall_ms=10\tcache_hits=0\tcpu_unavailable=0",
            "origin=pre-review-evidence\tbuilds=1\tcpu_ms=500\twall_ms=10\tcache_hits=0\tcpu_unavailable=0",
            "origin=train\tbuilds=1\tcpu_ms=200\twall_ms=10\tcache_hits=1\tcpu_unavailable=0",
            "repeated_trees=1\trepeat_builds=1\trepeat_cpu_ms=3000",
            "tree=wbc1:git-tree:a\tcompiles=2\torigins=gate,worker-check\trepeat_cpu_ms=3000",
            "lines=4\tskipped=0"
        }, Report());
    }

    [Fact]
    public void Builds_FiltersByRecordedTimeAndSkipsMalformedRows()
    {
        Write(Row("gate", "a", 100, "miss", "0001-01-01T00:00:01Z"),
            Row("train", "a", 200, "miss", "9999-12-31T23:59:59Z"), "{broken", "{}");
        Assert.Equal(new[]
        {
            "origin=train\tbuilds=1\tcpu_ms=200\twall_ms=10\tcache_hits=0\tcpu_unavailable=0",
            "repeated_trees=0\trepeat_builds=0\trepeat_cpu_ms=0",
            "lines=4\tskipped=2"
        }, Report("--last-hours", "24"));
    }

    [Fact]
    public void Builds_DispatchesAsAReadOnlyQueryWithMissingLedger()
    {
        string[] args = ["gate-stats", "builds"];
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var workspace = OrchestratorWorkspace.ForDirectory(_root);
        var output = CaptureConsole(() => CliGateStatsQueryCommand.Execute(args, workspace));
        Assert.Contains("repeated_trees=0\trepeat_builds=0\trepeat_cpu_ms=0", output);
        Assert.Contains("lines=0\tskipped=0", output);
        Assert.False(Directory.Exists(workspace.OrchestratorDirectory));
        Assert.Contains("gate-stats builds [--last-hours <n>]", CliCommandHelp.GateStatsUsage);
    }

    [Theory]
    [InlineData("--last-hours")]
    [InlineData("--last-hours", "0")]
    [InlineData("--last-hours", "-1")]
    [InlineData("--last-hours", "NaN")]
    [InlineData("--last", "2")]
    [InlineData("--last-hours", "2", "--last-hours", "3")]
    public void Builds_RejectsInvalidArguments(params string[] flags) =>
        Assert.Equal(CliCommandHelp.GateStatsUsage, Assert.Throws<ArgumentException>(() => Report(flags)).Message);

    private void Write(params string[] lines)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllLines(Path.Combine(_root, "build-receipts.jsonl"), lines);
    }

    private string[] Report(params string[] flags)
    {
        using var output = new StringWriter();
        CliGateStatsBuildsCommand.Execute(["gate-stats", "builds", .. flags], _root, output);
        return output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Row(string origin, string tree, int cpu, string cache, string at) =>
        $$"""{"version":1,"recorded_at":"{{at}}","goal_id":"goal","origin":"{{origin}}","tree_digest":"wbc1:git-tree:{{tree}}","projects":["app.csproj"],"wall_ms":10,"cpu_ms":{{cpu}},"cpu_ms_unavailable_reason":null,"cache":"{{cache}}"}""";

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
