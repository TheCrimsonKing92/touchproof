using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Xunit;

// Parallel-safe: owned artifact folders, explicit writers, and AsyncLocal console capture.
public sealed class GateStatsMemoryTests : CliTaskQueryTestSupport
{
    [Fact]
    public void Memory_RanksAttemptMediansAndSkipsMalformedRows()
    {
        using var fixture = new Fixture();
        fixture.Write(1, "alpha", Line(42, "A", 100, 90), Line(42, "B", 500, -10));
        fixture.Write(2, "alpha", Line(42, "A", 300, 60), Line(42, "B", 51, 5));
        fixture.Write(3, "alpha", Line(42, "A", 900, 900));
        var lastLane = fixture.Write(3, "beta", Line(42, "A", 150, -30), Line(51, "A", 50, -10));
        const string classB = "class=B\tgates=2\tmedian_heap_growth_bytes=275.5\tmedian_private_growth_bytes=-2.5";
        const string classA = "class=A\tgates=3\tmedian_heap_growth_bytes=200\tmedian_private_growth_bytes=60";
        Assert.Equal(new[] { classB, classA, "attempts=3\tskipped=0" }, fixture.Report());

        File.AppendAllLines(lastLane, [Line(42, "A", 999, 0).Replace("private_end_bytes=1000", "private_end_bytes=-1")]);

        Assert.Equal(new[] { classB, classA, "attempts=3\tskipped=1" }, fixture.Report());
        Assert.Equal(new[]
        {
            "class=A\tgates=1\tmedian_heap_growth_bytes=200\tmedian_private_growth_bytes=-40",
            "attempts=1\tskipped=1"
        }, fixture.Report("--last", "1", "--top", "1"));
        Assert.Equal(new[] { classB, "attempts=3\tskipped=1" }, fixture.Report("--top", "1"));
    }

    [Fact]
    public void Memory_KeepsNegativeHeapGrowthAndOrdersTiesByClass()
    {
        using var fixture = new Fixture();
        fixture.Write(1, "alpha", Line(42, "Z", -5, 2), Line(42, "A", -5, 1));
        fixture.Write(1, "legacy");

        Assert.Equal(new[]
        {
            "class=A\tgates=1\tmedian_heap_growth_bytes=-5\tmedian_private_growth_bytes=1",
            "class=Z\tgates=1\tmedian_heap_growth_bytes=-5\tmedian_private_growth_bytes=2",
            "attempts=1\tskipped=1"
        }, fixture.Report());
    }

    [Fact]
    public void Memory_QueryRouteDispatchesWithoutWrites()
    {
        using var fixture = new Fixture();
        string[] args = ["gate-stats", "memory", "--last", "5", "--top", "3"];
        Assert.True(CliGateStatsQueryCommand.IsGateStatsQueryCommand(args));
        CliCommandHelp.ThrowIfInvalidFlags(args);
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);

        Assert.Equal("attempts=0\tskipped=0" + Environment.NewLine,
            CaptureConsole(() => CliGateStatsQueryCommand.Execute(args, workspace)));
        Assert.False(Directory.Exists(workspace.OrchestratorDirectory));
        Assert.Contains("gate-stats memory [--last <n>] [--top <k>]", CliCommandHelp.GateStatsUsage);
    }

    [Theory]
    [InlineData("--last")]
    [InlineData("--top", "0")]
    [InlineData("--last", "1", "--last", "2")]
    [InlineData("--json")]
    public void Memory_InvalidFlagsReportUsage(params string[] flags)
    {
        using var fixture = new Fixture();
        Assert.Equal(CliCommandHelp.GateStatsUsage, Assert.Throws<ArgumentException>(() => fixture.Report(flags)).Message);
    }

    private static string Line(int pid, string name, int heapGrowth, int privateGrowth) => FormattableString.Invariant(
        $"LAUNCH_LOCK_MEMORY_BY_CLASS pid={pid} class={name} heap_start_bytes=1000 heap_end_bytes={1000 + heapGrowth} private_start_bytes=1000 private_end_bytes={1000 + privateGrowth}");

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CreateTempDirectory();

        internal string Write(int day, string lane, params string[] lines)
        {
            var folder = Path.Combine(Root, "acceptance-gate-attempts", "goal");
            Directory.CreateDirectory(folder);
            var name = FormattableString.Invariant($"attempt-0-202611{day:D2}120000000-00000000000000000000000000000000.{lane}.launch-lock-summary.txt");
            var path = Path.Combine(folder, name);
            File.WriteAllLines(path, ["LAUNCH_LOCK_SUMMARY pid=42 launches=100", .. lines]);
            return path;
        }

        internal string[] Report(params string[] flags)
        {
            using var output = new StringWriter();
            CliGateStatsMemoryCommand.Execute(["gate-stats", "memory", .. flags], Root, output);
            return output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
