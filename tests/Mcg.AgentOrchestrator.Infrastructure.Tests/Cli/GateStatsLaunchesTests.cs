using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: uniquely owned artifact folders, explicit writers and AsyncLocal console capture.
public sealed class GateStatsLaunchesTests : CliTaskQueryTestSupport
{
    [Fact]
    public void Launches_TwoGatesTwoLanesSumCountsAndCreateTimeInLaunchOrder()
    {
        using var fixture = new Fixture();
        fixture.Write("goal-a", 1, "alpha", Line("A", 3, 2, "1.250"), Line("B", 10, 8, "2.500"));
        fixture.Write("goal-a", 1, "beta", Line("A", 4, 1, "3.125"));
        fixture.Write("goal-b", 2, "alpha", Line("A", 5, 4, "4.500"), Line("B", 2, 1, "5.250"));
        fixture.Write("goal-b", 2, "beta", Line("A", 6, 3, "6.000"), Line("C", 12, 0, "7.000"));
        Assert.Equal(new[]
        {
            "class=A\tgates=2\tlaunches=18\tgit_cli_launches=10\tcreate_ms_total=14.875",
            "class=B\tgates=2\tlaunches=12\tgit_cli_launches=9\tcreate_ms_total=7.750",
            "class=C\tgates=1\tlaunches=12\tgit_cli_launches=0\tcreate_ms_total=7.000",
            "attempts=2\tskipped=0"
        }, fixture.Report());
        Assert.Equal(new[]
        {
            "class=C\tgates=1\tlaunches=12\tgit_cli_launches=0\tcreate_ms_total=7.000",
            "class=A\tgates=1\tlaunches=11\tgit_cli_launches=7\tcreate_ms_total=10.500",
            "class=B\tgates=1\tlaunches=2\tgit_cli_launches=1\tcreate_ms_total=5.250",
            "attempts=1\tskipped=0"
        }, fixture.Report("--last", "1"));
        Assert.Equal(new[]
        {
            "class=A\tgates=2\tlaunches=18\tgit_cli_launches=10\tcreate_ms_total=14.875",
            "attempts=2\tskipped=0"
        }, fixture.Report("--top", "1"));
    }

    [Fact]
    public void Launches_GoalFolderIsPartOfAttemptIdentityAndRetriesCount()
    {
        using var fixture = new Fixture();
        fixture.Write("one", 1, "alpha", Line("A", 1, 1, "1.000"));
        fixture.Write("one", 1, "alpha-run-1", Line("A", 2, 2, "2.000"));
        fixture.Write("two", 1, "alpha", Line("A", 3, 3, "3.000"));
        Assert.Equal(new[]
        {
            "class=A\tgates=2\tlaunches=6\tgit_cli_launches=6\tcreate_ms_total=6.000",
            "attempts=2\tskipped=0"
        }, fixture.Report());
    }

    [Fact]
    public void Launches_MalformedAndUnreadableEvidenceIsSkippedWithoutLosingGoodRows()
    {
        using var fixture = new Fixture();
        fixture.Write("goal", 1, "alpha", Line("A", 3, 2, "1.500"),
            "LAUNCH_LOCK_BY_CLASS invalid", Line("Bad", 1, 1, "NaN"),
            Line("Bad", 1, 1, "1.000").Replace("launches=1", "launches=-1"));
        var locked = fixture.Write("goal", 1, "beta", Line("B", 100, 100, "100.000"));
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(new[]
        {
            "class=A\tgates=1\tlaunches=3\tgit_cli_launches=2\tcreate_ms_total=1.500",
            "attempts=1\tskipped=4"
        }, fixture.Report());
    }

    [Fact]
    public void Launches_IsRegisteredOnTheQueryRouteAndDispatchesWithoutWrites()
    {
        using var fixture = new Fixture();
        string[] args = ["gate-stats", "launches", "--last", "5", "--top", "3"];
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.True(CliGateStatsQueryCommand.IsGateStatsQueryCommand(args));
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var output = CaptureConsole(() => CliGateStatsQueryCommand.Execute(args, workspace));
        Assert.Equal("attempts=0\tskipped=0" + Environment.NewLine, output);
        Assert.False(Directory.Exists(workspace.OrchestratorDirectory));
        Assert.Contains("gate-stats launches [--last <n>] [--top <k>]", CliCommandHelp.GateStatsUsage);
    }

    [Theory]
    [InlineData("--last")]
    [InlineData("--last", "0")]
    [InlineData("--last", "-1")]
    [InlineData("--last", "2147483648")]
    [InlineData("--top", "no")]
    [InlineData("--top", "1", "--top", "2")]
    [InlineData("--last", "1", "--last", "2")]
    [InlineData("--unknown", "1")]
    [InlineData("--json")]
    public void Launches_InvalidFlagsReportUsageWithoutCreatingState(params string[] flags)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        var missing = Path.Combine(fixture.Root, "missing");
        var error = Assert.Throws<ArgumentException>(() =>
            CliGateStatsLaunchesCommand.Execute(["gate-stats", "launches", .. flags], missing, output));
        Assert.Equal(CliCommandHelp.GateStatsUsage, error.Message);
        Assert.Equal("", output.ToString());
        Assert.False(Directory.Exists(missing));
    }

    private static string Line(string name, long launches, long git, string milliseconds) =>
        $"LAUNCH_LOCK_BY_CLASS pid=42 class={name} launches={launches} create_ms_total={milliseconds} " +
        $"by_entry=AcquireConsoleForChildSpawn:0,AcquireSuppressedChildSpawn:0,AcquireErrorModeForChildSpawn:0,Start:{launches - git},GitCli:{git}";

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CreateTempDirectory();
        internal string Write(string goal, int day, string lane, params string[] lines)
        {
            var folder = Path.Combine(Root, "acceptance-gate-attempts", goal);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, $"attempt-0-202611{day:D2}120000000-00000000000000000000000000000000.{lane}.launch-lock-summary.txt");
            File.WriteAllLines(path, ["LAUNCH_LOCK_SUMMARY pid=42 launches=100", .. lines]);
            return path;
        }
        internal string[] Report(params string[] flags)
        {
            using var output = new StringWriter();
            CliGateStatsLaunchesCommand.Execute(["gate-stats", "launches", .. flags], Root, output);
            return output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
