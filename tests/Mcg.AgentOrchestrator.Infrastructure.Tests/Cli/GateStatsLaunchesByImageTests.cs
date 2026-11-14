using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: uniquely owned artifact folders and AsyncLocal console capture.
public sealed class GateStatsLaunchesByImageTests : CliTaskQueryTestSupport
{
    [Fact]
    public void Launches_TwoAttempts_RanksSummedImagesWithOrdinalTies()
    {
        using var fixture = new Fixture();
        fixture.Write("goal-a", 1, "alpha",
            Line("A", 42, 2, 1, 0, 1, 0, 0, 1), Line("B", 42, 0, 0, 2, 0, 1, 0, 1));
        fixture.Write("goal-a", 1, "beta", Line("A", 42, 1, 0, 1, 0, 0, 1, 0));
        fixture.Write("goal-b", 2, "alpha", Line("A", 7, 3, 1, 0, 0, 0, 0, 0));
        var path = fixture.Write("goal-b", 2, "beta",
            Line("B", 42, 0, 0, 0, 1, 1, 1, 1), Line("C", 42, 8, 0, 0, 0, 0, 0, 0));
        string[] rows =
        [
            "class=A\ttotal=12\tgit=6\tpowershell=2\tpwsh=1\ticacls=1\tcmd=0\tdotnet=1\tother=1",
            "class=B\ttotal=8\tgit=0\tpowershell=0\tpwsh=2\ticacls=1\tcmd=2\tdotnet=1\tother=2",
            "class=C\ttotal=8\tgit=8\tpowershell=0\tpwsh=0\ticacls=0\tcmd=0\tdotnet=0\tother=0"
        ];
        Assert.Equal([.. rows, "attempts=2\tskipped=0"], fixture.Report("--by-image"));
        Assert.Equal(new[] { rows[0], "attempts=2\tskipped=0" }, fixture.Report("--by-image", "--top", "1"));
        Assert.Equal(new[]
        {
            rows[2],
            "class=A\ttotal=4\tgit=3\tpowershell=1\tpwsh=0\ticacls=0\tcmd=0\tdotnet=0\tother=0",
            "class=B\ttotal=4\tgit=0\tpowershell=0\tpwsh=0\ticacls=1\tcmd=1\tdotnet=1\tother=1",
            "attempts=1\tskipped=0"
        }, fixture.Report("--by-image", "--last", "1"));
        // A late invalid field must not add the earlier valid counts to A.
        File.AppendAllLines(path, [Line("A", 42, 100, 0, 0, 0, 0, 0, 0).Replace("other=0", "other=bad")]);
        Assert.Equal([.. rows, "attempts=2\tskipped=1"], fixture.Report("--by-image"));
    }

    [Fact]
    public void Launches_RegisteredFlag_QueryRouteMatchesImageOutput()
    {
        using var fixture = new Fixture();
        fixture.Write("goal", 1, "alpha", Line("A", 42, 1, 0, 0, 0, 0, 0, 0));
        string[] args = ["gate-stats", "launches", "--by-image"];
        CliCommandHelp.ThrowIfInvalidFlags(args);
        Assert.Equal(CliCommandCapability.QueryOnly, CliCommandCapabilities.Classify(args));
        Assert.True(CliReadOnlyCommandRunner.IsReadOnlyCommand(args));
        Assert.True(CliGateStatsQueryCommand.IsGateStatsQueryCommand(args));
        var output = CaptureConsole(() => CliGateStatsQueryCommand.Execute(args, fixture.Workspace));
        Assert.Equal(string.Join(Environment.NewLine, fixture.Report("--by-image")) + Environment.NewLine, output);
        Assert.Contains("[--by-image]", CliCommandHelp.GateStatsUsage);
    }

    [Fact]
    public void Launches_HistoricalAndMixedRecords_PreservesDefaultBytes()
    {
        using var fixture = new Fixture();
        var path = fixture.Write("goal", 1, "alpha",
            "LAUNCH_LOCK_BY_CLASS pid=42 class=A launches=3 create_ms_total=1.000 by_entry=GitCli:2");
        var original = fixture.Report();
        Assert.Equal(new[]
        {
            "class=A\tgates=1\tlaunches=3\tgit_cli_launches=2\tcreate_ms_total=1.000", "attempts=1\tskipped=0"
        }, original);
        Assert.Equal(new[] { "attempts=1\tskipped=0" }, fixture.Report("--by-image"));
        File.AppendAllLines(path, [Line("A", 42, 2, 0, 0, 1, 0, 0, 0), "LAUNCH_LOCK_BY_IMAGE invalid"]);
        Assert.Equal(original, fixture.Report());
        Assert.Equal(new[]
        {
            "class=A\ttotal=3\tgit=2\tpowershell=0\tpwsh=0\ticacls=1\tcmd=0\tdotnet=0\tother=0",
            "attempts=1\tskipped=1"
        }, fixture.Report("--by-image"));
    }

    [Theory]
    [InlineData("--by-image", "--by-image")]
    [InlineData("--by-image", "--by-site")]
    [InlineData("--by-site", "--by-image")]
    [InlineData("--by-image", "--class", "A")]
    [InlineData("--by-image", "--json")]
    [InlineData("--by-image", "--last", "0")]
    [InlineData("--by-image", "--top", "1", "--top", "2")]
    public void Launches_InvalidFlags_ThrowsUsageBeforeWriting(params string[] flags)
    {
        using var fixture = new Fixture();
        using var output = new StringWriter();
        var error = Assert.Throws<ArgumentException>(() => CliGateStatsLaunchesCommand.Execute(
            ["gate-stats", "launches", .. flags], fixture.Workspace.OrchestratorDirectory, output));
        Assert.Equal(CliCommandHelp.GateStatsUsage, error.Message);
        Assert.Equal("", output.ToString());
        Assert.False(Directory.Exists(fixture.Workspace.OrchestratorDirectory));
    }

    public static TheoryData<string> ImageFields => new()
    {
        "git", "powershell", "pwsh", "icacls", "cmd", "dotnet", "other"
    };

    [Theory]
    [MemberData(nameof(ImageFields))]
    public void Launches_MalformedRequiredCount_SkipsEntireRecord(string field)
    {
        using var fixture = new Fixture();
        var valid = Line("A", 42, 1, 1, 1, 1, 1, 1, 1);
        var token = field + "=1";
        string[] malformed =
        [
            valid.Replace(token, ""), valid + " " + token, valid.Replace(token, field + "=-1"),
            valid.Replace(token, field + "=bad"), valid.Replace(token, field + "=9223372036854775808"),
            valid.Replace(token, field + "=")
        ];
        fixture.Write("goal", 1, "alpha", [valid, .. malformed]);
        Assert.Equal(new[]
        {
            "class=A\ttotal=7\tgit=1\tpowershell=1\tpwsh=1\ticacls=1\tcmd=1\tdotnet=1\tother=1",
            "attempts=1\tskipped=6"
        }, fixture.Report("--by-image"));
    }

    [Theory]
    [InlineData("pid=42", "pid=no")]
    [InlineData("pid=42", "pid=-1")]
    [InlineData("pid=42", "pid=2147483648")]
    [InlineData("pid=42", "")]
    [InlineData("pid=42", "pid=42 pid=7")]
    [InlineData("class=A", "class=")]
    [InlineData("class=A", "class=A\tB")]
    [InlineData("class=A", "class=A class=B")]
    [InlineData("class=A", "")]
    [InlineData("class=A", "class=A invalid-token")]
    public void Launches_MalformedMetadata_SkipsRecordWithoutChangingRows(string from, string to)
    {
        using var fixture = new Fixture();
        var valid = Line("A", 42, 1, 0, 0, 0, 0, 0, 0);
        fixture.Write("goal", 1, "alpha", valid, valid.Replace(from, to), "LAUNCH_LOCK_BY_CLASS invalid");
        Assert.Equal(new[]
        {
            "class=A\ttotal=1\tgit=1\tpowershell=0\tpwsh=0\ticacls=0\tcmd=0\tdotnet=0\tother=0",
            "attempts=1\tskipped=1"
        }, fixture.Report("--by-image"));
    }

    [Fact]
    public void Launches_RepeatedRecordsAndExtraFields_SumsWithoutOverflow()
    {
        using var fixture = new Fixture();
        var line = Line("A", 42, long.MaxValue, 0, 0, 0, 0, 0, 0) + " future=value";
        fixture.Write("goal", 1, "alpha", line, line,
            Line("Zero", 0, 0, 0, 0, 0, 0, 0, 0));
        Assert.Equal(new[]
        {
            "class=A\ttotal=18446744073709551614\tgit=18446744073709551614\tpowershell=0\tpwsh=0\ticacls=0\tcmd=0\tdotnet=0\tother=0",
            "class=Zero\ttotal=0\tgit=0\tpowershell=0\tpwsh=0\ticacls=0\tcmd=0\tdotnet=0\tother=0",
            "attempts=1\tskipped=0"
        }, fixture.Report("--by-image"));
    }

    private static string Line(string name, int pid, params long[] counts) =>
        $"LAUNCH_LOCK_BY_IMAGE pid={pid} class={name} git={counts[0]} powershell={counts[1]} pwsh={counts[2]} icacls={counts[3]} cmd={counts[4]} dotnet={counts[5]} other={counts[6]}";

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CreateTempDirectory();
        internal OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(Root);

        internal string Write(string goal, int day, string lane, params string[] lines)
        {
            var directory = Path.Combine(Workspace.OrchestratorDirectory, "acceptance-gate-attempts", goal);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory,
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
