using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: real Git contracts use test-owned repositories; classifier rows mutate no shared state.
public sealed class GoalWorktreeCreationProbeContractTests : GoalWorktreeTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CombinedProbe_LocalBranch_PinsExitAndOutput(bool present)
    {
        var repo = CreateSeededRepository();
        try
        {
            const string branch = "goal/contract";
            if (present) RunGit(repo, "branch", branch);
            Assert.Equal(present, BranchExists(repo, branch));
            var args = GoalWorktreeCreationProbe.Arguments(branch);
            Assert.Equal(new[] { "rev-parse", "--is-inside-work-tree", "--absolute-git-dir", "--verify", "--quiet", "refs/heads/goal/contract" }, args);
            var result = GitCli.Run(repo, args);
            Assert.True(result.ProcessStarted);
            Assert.False(result.DrainTimedOut);
            Assert.Equal(present ? 0 : 1, result.ExitCode);
            Assert.Empty(result.Error);
            var lines = result.Output.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
            Assert.Equal(present ? 3 : 2, lines.Length);
            Assert.Equal("true", lines[0]);
            Assert.Equal(Path.GetFullPath(Path.Combine(repo, ".git")), Path.GetFullPath(lines[1]));
            if (present) Assert.Equal(RunGitOutput(repo, "rev-parse", "refs/heads/" + branch).Trim(), lines[2]);
            var facts = Assert.IsType<GoalWorktreeCreationProbe.Facts>(GoalWorktreeCreationProbe.Classify(result));
            Assert.Equal(present, facts.BranchExists);
            Assert.Equal(Path.GetFullPath(Path.Combine(repo, ".git")), facts.GitDirectory);
        }
        finally { DeleteDirectory(repo); }
    }

    [Fact]
    public void CombinedProbe_NotAWorkTree_PinsFailureContract()
    {
        var repo = CreateSeededRepository();
        try
        {
            var directory = Directory.CreateDirectory(Path.Combine(repo, "not-work-tree")).FullName;
            File.WriteAllText(Path.Combine(directory, ".git"), "gitdir: missing\n");
            var result = GitCli.Run(directory, GoalWorktreeCreationProbe.Arguments("missing"));
            Assert.True(result.ProcessStarted);
            Assert.False(result.DrainTimedOut);
            Assert.Equal(128, result.ExitCode);
            Assert.Empty(result.Output);
            Assert.Contains("not a git repository", result.Error);
            Assert.Null(GoalWorktreeCreationProbe.Classify(result));
        }
        finally { DeleteDirectory(repo); }
    }

    [Theory]
    [InlineData("stderr")]
    [InlineData("drain-timeout")]
    [InlineData("not-started")]
    [InlineData("extra-line")]
    [InlineData("missing-line")]
    [InlineData("relative-directory")]
    [InlineData("missing-directory")]
    [InlineData("false")]
    [InlineData("unknown-status")]
    [InlineData("bad-object-id")]
    [InlineData("missing-object-id")]
    public void Classify_UnrecognizedResult_RemainsUndecided(string shape)
    {
        var directory = Path.GetTempPath();
        var result = new GitCli.GitResult(1, $"true\n{directory}\n", "");
        result = shape switch
        {
            "stderr" => result with { Error = "fatal: invalid configuration" },
            "drain-timeout" => result with { DrainTimedOut = true },
            "not-started" => result with { ProcessStarted = false },
            "extra-line" => result with { Output = result.Output + "extra\n" },
            "missing-line" => result with { Output = "true\n" },
            "relative-directory" => result with { Output = "true\n.git\n" },
            "missing-directory" => result with { Output = $"true\n{Path.Combine(directory, Guid.NewGuid().ToString("N"))}\n" },
            "false" => result with { Output = $"false\n{directory}\n" },
            "unknown-status" => result with { ExitCode = 128 },
            "bad-object-id" => result with { ExitCode = 0, Output = result.Output + "not-an-object\n" },
            "missing-object-id" => result with { ExitCode = 0 },
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
        Assert.Null(GoalWorktreeCreationProbe.Classify(result));
    }
}
