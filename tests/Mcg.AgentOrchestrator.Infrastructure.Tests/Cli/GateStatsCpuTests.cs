using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: owned artifact folders, explicit writers, and AsyncLocal console capture.
public sealed class GateStatsCpuTests : CliTaskQueryTestSupport
{
    [Fact]
    public void Cpu_RanksByChildCpuAndShowsActiveShareAcrossGates()
    {
        using var fixture = new Fixture();
        fixture.Write("one", 1, "alpha", Line("A", "10.125", "0.250"), Line("Z", "0", "99999"), Line("B", "0", "95000"));
        fixture.Write("one", 1, "beta", Line("A", "0.125", "1.250"), Line("D", "0.200", "0"));
        fixture.Write("two", 2, "alpha", Line("A", "2.250", "0"), Line("C", "25", "25"));
        fixture.Write("two", 2, "legacy");
        Assert.Equal(new[]
        {
            "class=C\tgates=1\tchild_cpu_ms=25.000\tinproc_active_share_ms=25.000",
            "class=A\tgates=2\tchild_cpu_ms=12.500\tinproc_active_share_ms=1.500",
            "class=D\tgates=1\tchild_cpu_ms=0.200\tinproc_active_share_ms=0.000",
            "class=B\tgates=1\tchild_cpu_ms=0.000\tinproc_active_share_ms=95000.000",
            "class=Z\tgates=1\tchild_cpu_ms=0.000\tinproc_active_share_ms=99999.000",
            "attempts=2\tskipped=1"
        }, fixture.Report());
        Assert.Equal(new[]
        {
            "class=C\tgates=1\tchild_cpu_ms=25.000\tinproc_active_share_ms=25.000",
            "attempts=1\tskipped=1"
        }, fixture.Report("--last", "1", "--top", "1"));
    }

    [Fact]
    public void Cpu_SkipsMalformedUnreadableAndLegacySummaries()
    {
        using var fixture = new Fixture();
        fixture.Write("one", 1, "alpha", Line("A", "1.25", "2.5"), "LAUNCH_LOCK_CPU_BY_CLASS invalid",
            Line("Bad", "NaN", "0"), Line("Bad", "-1", "0"), Line("Bad", "0", "Infinity"));
        fixture.Write("one", 1, "legacy");
        var locked = fixture.Write("one", 1, "locked", Line("Bad", "999", "0"));
        using var holder = new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(new[]
        {
            "class=A\tgates=1\tchild_cpu_ms=1.250\tinproc_active_share_ms=2.500",
            "attempts=1\tskipped=6"
        }, fixture.Report());
    }

    [Fact]
    public void Cpu_GroupsByGoalAndCountsRetryLaneCpuOncePerFile()
    {
        using var fixture = new Fixture();
        fixture.Write("one", 1, "alpha", Line("A", "1", "0"));
        fixture.Write("one", 1, "alpha-run-1", Line("A", "2", "0"));
        fixture.Write("two", 1, "alpha", Line("A", "3", "0"));
        Assert.Equal(new[]
        {
            "class=A\tgates=2\tchild_cpu_ms=6.000\tinproc_active_share_ms=0.000",
            "attempts=2\tskipped=0"
        }, fixture.Report());
    }

    [Fact]
    public void Cpu_QueryRouteDispatchesWithoutWrites()
    {
        using var fixture = new Fixture();
        string[] args = ["gate-stats", "cpu", "--last", "5", "--top", "3"];
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.True(CliGateStatsQueryCommand.IsGateStatsQueryCommand(args));
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        Assert.Equal("attempts=0\tskipped=0" + Environment.NewLine,
            CaptureConsole(() => CliGateStatsQueryCommand.Execute(args, workspace)));
        Assert.False(Directory.Exists(workspace.OrchestratorDirectory));
        Assert.Contains("gate-stats cpu [--last <n>] [--top <k>]", CliCommandHelp.GateStatsUsage);
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
    public void Cpu_InvalidFlagsReportUsageWithoutCreatingState(params string[] flags)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        var missing = Path.Combine(fixture.Root, "missing");
        var error = Assert.Throws<ArgumentException>(() =>
            CliGateStatsCpuCommand.Execute(["gate-stats", "cpu", .. flags], missing, output));
        Assert.Equal(CliCommandHelp.GateStatsUsage, error.Message);
        Assert.Equal("", output.ToString());
        Assert.False(Directory.Exists(missing));
    }

    private static string Line(string name, string child, string inProcess) =>
        $"LAUNCH_LOCK_CPU_BY_CLASS pid=42 class={name} child_cpu_ms={child} inproc_cpu_ms_apportioned={inProcess}";

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
            CliGateStatsCpuCommand.Execute(["gate-stats", "cpu", .. flags], Root, output);
            return output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
