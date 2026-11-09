using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: uniquely owned artifact folders and AsyncLocal console capture.
public sealed class GateStatsLaunchesBySiteTests : CliTaskQueryTestSupport
{
    [Fact]
    public void Launches_BySite_SumsAcrossGatesAndLanesInDescendingOrder()
    {
        using var fixture = PopulatedFixture();
        Assert.Equal(new[]
        {
            "site=Helper.Git\tgit=status\tlaunches=18",
            "site=C.Run\tgit=-\tlaunches=12",
            "site=Other.Run\tgit=-\tlaunches=12",
            "site=Helper.Git\tgit=diff\tlaunches=2",
            "site=Helper.Git\tgit=log\tlaunches=2",
            "attempts=2\tskipped=0"
        }, fixture.Report("--by-site"));
        Assert.Equal(new[]
        {
            "site=Helper.Git\tgit=status\tlaunches=8",
            "site=Helper.Git\tgit=diff\tlaunches=2",
            "site=Helper.Git\tgit=log\tlaunches=2",
            "attempts=2\tskipped=0"
        }, fixture.Report("--by-site", "--class", "A"));
        Assert.Equal(new[] { "attempts=2\tskipped=0" }, fixture.Report("--by-site", "--class", "a"));
        Assert.Equal(new[]
        {
            "site=C.Run\tgit=-\tlaunches=12",
            "site=Helper.Git\tgit=status\tlaunches=11",
            "site=Helper.Git\tgit=diff\tlaunches=2",
            "site=Other.Run\tgit=-\tlaunches=2",
            "attempts=1\tskipped=0"
        }, fixture.Report("--by-site", "--last", "1"));
        Assert.Equal(new[]
        {
            "site=Helper.Git\tgit=status\tlaunches=18", "attempts=2\tskipped=0"
        }, fixture.Report("--by-site", "--top", "1"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Launches_RegisteredFlags_QueryRouteMatchesFixtureTotals(bool filter)
    {
        using var fixture = PopulatedFixture();
        string[] flags = filter ? ["--by-site", "--class", "A", "--last", "3"] : ["--by-site", "--last", "3"];
        string[] args = ["gate-stats", "launches", .. flags];
        CliCommandHelp.ThrowIfInvalidFlags(args);
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.True(CliGateStatsQueryCommand.IsGateStatsQueryCommand(args));
        var output = CaptureConsole(() => CliGateStatsQueryCommand.Execute(args, fixture.Workspace));
        Assert.Equal(string.Join(Environment.NewLine, fixture.Report(flags)) + Environment.NewLine, output);
        Assert.Contains("gate-stats launches [--last <n>] [--top <k>] [--by-site [--class <name>]]",
            CliCommandHelp.GateStatsUsage);
    }

    [Fact]
    public void Launches_MixedAndHistoricalSummaries_PreservesDefaultOutput()
    {
        using var fixture = new Fixture();
        const string legacy = "LAUNCH_LOCK_BY_CLASS pid=42 class=A launches=3 create_ms_total=1.000 by_entry=GitCli:2";
        var path = fixture.Write("goal", 1, "alpha", legacy);
        var original = fixture.Report();
        Assert.Equal(new[]
        {
            "class=A\tgates=1\tlaunches=3\tgit_cli_launches=2\tcreate_ms_total=1.000", "attempts=1\tskipped=0"
        }, original);
        Assert.Equal(new[] { "attempts=1\tskipped=0" }, fixture.Report("--by-site"));
        File.AppendAllLines(path, [Line("A", "Helper.Git", "status", 3), "LAUNCH_LOCK_BY_SITE invalid"]);
        Assert.Equal(original, fixture.Report());
        Assert.Equal(new[]
        {
            "site=Helper.Git\tgit=status\tlaunches=3", "attempts=1\tskipped=1"
        }, fixture.Report("--by-site"));
    }

    [Theory]
    [InlineData("--by-site", "--by-site")]
    [InlineData("--class", "A")]
    [InlineData("--by-site", "--class")]
    [InlineData("--by-site", "--class", "")]
    [InlineData("--by-site", "--class", " ")]
    [InlineData("--class", "--by-site")]
    [InlineData("--by-site", "--class", "A", "--class", "B")]
    [InlineData("--by-site", "--last", "0")]
    [InlineData("--by-site", "--top", "1", "--top", "2")]
    [InlineData("--by-site", "--json")]
    public void Launches_InvalidSiteFlags_ReportsUsageWithoutWriting(params string[] flags)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        var error = Assert.Throws<ArgumentException>(() =>
            CliGateStatsLaunchesCommand.Execute(["gate-stats", "launches", .. flags], fixture.Workspace.OrchestratorDirectory, output));
        Assert.Equal(CliCommandHelp.GateStatsUsage, error.Message);
        Assert.Equal("", output.ToString());
        Assert.False(Directory.Exists(fixture.Workspace.OrchestratorDirectory));
    }

    [Theory]
    [InlineData("LAUNCH_LOCK_BY_SITE invalid")]
    [InlineData("LAUNCH_LOCK_BY_SITE pid=no class=A site=X.Run git=status launches=1")]
    [InlineData("LAUNCH_LOCK_BY_SITE pid=42 class=A site=X.Run git=status launches=-1")]
    [InlineData("LAUNCH_LOCK_BY_SITE pid=42 class=A site=X.Run git=status launches=9223372036854775808")]
    [InlineData("LAUNCH_LOCK_BY_SITE pid=42 class=A site=X.Run git= launches=1")]
    [InlineData("LAUNCH_LOCK_BY_SITE pid=42 class=A site=X.Run site=Other.Run launches=1")]
    [InlineData("LAUNCH_LOCK_BY_SITE pid=42 class=A site=X.Run git=status launches=1 extra=1")]
    [InlineData("LAUNCH_LOCK_BY_SITE pid=42 class=A site=X.\tRun git=status launches=1")]
    public void Launches_MalformedSites_CountsSkippedAndKeepsValidRows(string malformed)
    {
        using var fixture = new Fixture();
        fixture.Write("goal", 1, "alpha", Line("A", "Helper.Git", "status", 3), malformed,
            "LAUNCH_LOCK_BY_CLASS invalid");
        Assert.Equal(new[]
        {
            "site=Helper.Git\tgit=status\tlaunches=3", "attempts=1\tskipped=1"
        }, fixture.Report("--by-site"));
    }

    private static Fixture PopulatedFixture()
    {
        var fixture = new Fixture();
        fixture.Write("goal-a", 1, "alpha", Line("A", "Helper.Git", "status", 3),
            Line("A", "Helper.Git", "log", 2), Line("B", "Other.Run", "-", 10));
        fixture.Write("goal-a", 1, "beta", Line("B", "Helper.Git", "status", 4));
        fixture.Write("goal-b", 2, "alpha", Line("A", "Helper.Git", "status", 5), Line("B", "Other.Run", "-", 2));
        fixture.Write("goal-b", 2, "beta", Line("B", "Helper.Git", "status", 6), Line("C", "C.Run", "-", 12),
            Line("A", "Helper.Git", "diff", 2));
        return fixture;
    }

    private static string Line(string name, string site, string git, long launches) =>
        $"LAUNCH_LOCK_BY_SITE pid=42 class={name} site={site} git={git} launches={launches}";

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CreateTempDirectory();
        internal OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(Root);

        internal string Write(string goal, int day, string lane, params string[] lines)
        {
            var folder = Path.Combine(Workspace.OrchestratorDirectory, "acceptance-gate-attempts", goal);
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder,
                $"attempt-0-202611{day:D2}120000000-00000000000000000000000000000000.{lane}.launch-lock-summary.txt");
            File.WriteAllLines(path, ["LAUNCH_LOCK_SUMMARY pid=42 launches=100", .. lines]);
            return path;
        }

        internal string[] Report(params string[] flags)
        {
            using var output = new StringWriter();
            CliGateStatsLaunchesCommand.Execute(["gate-stats", "launches", .. flags], Workspace.OrchestratorDirectory, output);
            return output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
