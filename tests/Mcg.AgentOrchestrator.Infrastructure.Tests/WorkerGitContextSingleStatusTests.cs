using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories and test-identity-scoped launch census windows.
public sealed class WorkerGitContextSingleStatusTests
{
    [Fact]
    public void ChangedFiles_FixtureStates_MatchOriginalTextAndLaunchCounts()
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        ConfigureStatus(fixture);
        var repo = fixture.Repo;
        string[] tracked = ["tracked.txt", "removed.txt", "space name.txt", "naïve.txt",
            "naïve space.txt", "tracked/leaf.txt"];
        foreach (var path in tracked) Write(repo, path, "baseline\n");
        fixture.Git("add", ".");
        fixture.Git("commit", "-m", "tracked files");
        fixture.Git("config", "core.quotePath", "true");
        fixture.Git("config", "status.showUntrackedFiles", "normal");

        AssertParity(repo, 1, "");
        Write(repo, "tracked.txt", "staged\n");
        fixture.Git("add", "tracked.txt");
        AssertParity(repo, 1, "M ");
        fixture.Git("reset", "--hard", "HEAD");

        Write(repo, "tracked.txt", "unstaged\n");
        AssertParity(repo, 1, " M");
        fixture.Git("reset", "--hard", "HEAD");

        File.Delete(Path.Combine(repo, "removed.txt"));
        AssertParity(repo, 1, " D");
        fixture.Git("add", "removed.txt");
        AssertParity(repo, 1, "D ");
        fixture.Git("reset", "--hard", "HEAD");

        Write(repo, "tracked.txt", "staged\n");
        fixture.Git("add", "tracked.txt");
        Write(repo, "tracked.txt", "baseline\n");
        Assert.Equal("", GitCli.Run(repo, "diff", "--name-only", "HEAD", "--").Output);
        AssertParity(repo, 3, "MM");
        Write(repo, "tracked.txt", "another worktree edit\n");
        Assert.NotEmpty(GitCli.Run(repo, "diff", "--name-only", "HEAD", "--").Output);
        AssertParity(repo, 3, "MM");
        fixture.Git("reset", "--hard", "HEAD");

        Write(repo, "new.txt", "untracked\n");
        Write(repo, "tracked/new.txt", "untracked in tracked folder\n");
        Write(repo, "newdir/one.txt", "untracked in new folder\n");
        Write(repo, "newdir/two.txt", "second file\n");
        Assert.Contains("?? newdir/", GitCli.Run(repo, "status", "--short").Output);
        AssertParity(repo, 1, "??");
        fixture.Git("config", "status.showUntrackedFiles", "no");
        AssertParity(repo, 1, "");
        fixture.Git("config", "status.showUntrackedFiles", "all");
        Assert.Contains("?? newdir/one.txt", GitCli.Run(repo, "status", "--short").Output);
        AssertParity(repo, 1, "??");
        fixture.Git("clean", "-fd");

        foreach (var path in tracked.Skip(2)) Write(repo, path, "changed\n");
        AssertParity(repo, 1, " M");
        fixture.Git("config", "core.quotePath", "false");
        AssertParity(repo, 1, " M");
        fixture.Git("add", ".");
        AssertParity(repo, 1, "M ");
        fixture.Git("reset", "--hard", "HEAD");

        fixture.Git("mv", "tracked.txt", "renamed.txt");
        AssertParity(repo, 3, "R ");
        fixture.Git("reset", "--hard", "HEAD");
        Write(repo, "added.txt", "added\n");
        fixture.Git("add", "added.txt");
        AssertParity(repo, 3, "A ");
    }

    [Fact]
    public void ChangedFiles_UnbornHeadAndSubmodules_UseOriginalCalls()
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture(commit: false);
        ConfigureStatus(fixture);
        Write(fixture.Repo, "added.txt", "new\n");
        fixture.Git("add", "added.txt");
        Assert.False(GitCli.Run(fixture.Repo, "diff", "--name-only", "HEAD", "--").Succeeded);
        AssertParity(fixture.Repo, 3, "A ", diffMayFail: true);
        fixture.Git("commit", "-m", "seed");
        Write(fixture.Repo, ".gitmodules", "");
        AssertParity(fixture.Repo, 3, "??");
    }

    [Fact]
    public void ChangedFiles_StatusFailure_RetainsOriginalEmptyResult()
    {
        var root = Directory.CreateTempSubdirectory("worker-status-failure-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".git"));
            Assert.False(GitCli.Run(root, "status", "--short").Succeeded);
            using var launches = new LaunchRecording();
            Assert.Empty(new WorkerGitContext().ReadChangedFilesForTestImpact(root));
            launches.AssertCommands("status", "diff", "status");
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("MM file\n")]
    [InlineData("AM file\n")]
    [InlineData("RM file\n")]
    [InlineData("R  old -> new\n")]
    [InlineData(" C file\n")]
    [InlineData("A  file\n")]
    [InlineData(" T file\n")]
    [InlineData("UU file\n")]
    [InlineData("AA file\n")]
    [InlineData("DD file\n")]
    [InlineData(" m module\n")]
    [InlineData("!! ignored\n")]
    [InlineData("\r\n## branch\n")]
    [InlineData("\u001b[31m M file\n")]
    [InlineData(" M unquoted space\n")]
    [InlineData(" M \"unterminated\n")]
    [InlineData(" M \"bad\\q\"\n")]
    [InlineData(" M \"bad\\12\"\n")]
    [InlineData(" M \"embedded\"quote\"\n")]
    [InlineData(" M ")]
    [InlineData("bad")]
    public void DeriveDiff_UnsupportedOrMalformedRecord_Declines(string status)
    {
        // No filesystem setup is needed: absence of .gitmodules is the only filesystem check.
        var unusedDirectory = Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N"));
        Assert.False(WorkerGitStatusChangeReader.TryDeriveDiffNameOnly(unusedDirectory, status, out var output));
        Assert.Equal("", output);
    }

    [Theory]
    [InlineData(" M plain.txt\n?? untracked/\n", "plain.txt")]
    [InlineData("M  \"space name.txt\"\r\n D removed.txt\r\n", "space name.txt\nremoved.txt")]
    [InlineData(" M \"na\\303\\257ve space.txt\"\n", "\"na\\303\\257ve space.txt\"")]
    [InlineData(" M \"naïve space.txt\"\n", "naïve space.txt")]
    [InlineData(" M \"tab\\tname\"\n", "\"tab\\tname\"")]
    [InlineData(" M \"quote\\\"name\"\n", "\"quote\\\"name\"")]
    [InlineData("", "")]
    public void DeriveDiff_ProvenRecords_PreserveDiffQuoting(string status, string expected)
    {
        var unusedDirectory = Path.Combine(Path.GetTempPath(), "absent-" + Guid.NewGuid().ToString("N"));
        Assert.True(WorkerGitStatusChangeReader.TryDeriveDiffNameOnly(unusedDirectory, status, out var output));
        Assert.Equal(expected, output);
    }

    private static void AssertParity(string repo, int launches, string columns, bool diffMayFail = false)
    {
        var diff = GitCli.Run(repo, "diff", "--name-only", "HEAD", "--");
        var status = GitCli.Run(repo, "status", "--short");
        Assert.Equal(!diffMayFail, diff.Succeeded);
        Assert.True(status.Succeeded, status.Error);
        if (columns.Length == 0) Assert.Empty(status.Output);
        else Assert.Contains(SplitRecords(status.Output), line => line.StartsWith(columns, StringComparison.Ordinal));
        var expected = OriginalChangedFiles(diff, status);
        using var recording = new LaunchRecording();
        var actual = new WorkerGitContext().ReadChangedFilesForTestImpact(repo);
        Assert.Equal(expected, actual);
        Assert.Equal(string.Join('\n', expected), string.Join('\n', actual));
        recording.AssertCommands(launches == 1 ? ["status"] : ["status", "diff", "status"]);
    }

    // Frozen original two-call merge algorithm: preserve even TrimEntries/line[3..] behavior.
    private static string[] OriginalChangedFiles(GitCli.GitResult diff, GitCli.GitResult status)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (diff.Succeeded)
        {
            foreach (var line in SplitOriginal(diff.Output)) files.Add(line);
        }
        if (status.Succeeded)
        {
            foreach (var line in SplitOriginal(status.Output))
            {
                if (line.Length < 4) continue;
                var path = line[3..].Trim();
                var renameIndex = path.IndexOf(" -> ", StringComparison.Ordinal);
                files.Add(renameIndex >= 0 ? path[(renameIndex + 4)..] : path);
            }
        }
        return files.Order(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static string[] SplitOriginal(string output) => output.ReplaceLineEndings("\n")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string[] SplitRecords(string output) => output.Replace("\r\n", "\n")
        .Split('\n', StringSplitOptions.RemoveEmptyEntries);

    private static void Write(string repo, string path, string text)
    {
        var fullPath = Path.Combine(repo, path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, text);
    }

    private static void ConfigureStatus(GitRefFileResolverParityTests.Fixture fixture)
    {
        fixture.Git("config", "color.status", "false");
        fixture.Git("config", "status.branch", "false");
        fixture.Git("config", "status.renames", "true");
        fixture.Git("config", "status.showUntrackedFiles", "normal");
    }

    private sealed class LaunchRecording : IDisposable
    {
        private readonly HostCapacitySlotGitCensus.Window window;
        private bool closed;

        internal LaunchRecording()
        {
            var identity = HostCapacitySlotTimingWriter.CurrentTestName();
            Assert.NotEqual("<unknown>", identity);
            HostCapacitySlotGitCensus.EnsureInstalled(nameof(WorkerGitContextSingleStatusTests));
            window = Assert.IsType<HostCapacitySlotGitCensus.Window>(HostCapacitySlotGitCensus.Shared.Open(identity));
        }

        internal void AssertCommands(params string[] expected)
        {
            var delta = Assert.IsType<HostCapacitySlotGitCensus.GitLaunchDelta>(window.Close());
            closed = true;
            Assert.Equal(expected.Length, delta.Launches);
            Assert.Equal(expected.GroupBy(command => command).ToDictionary(group => group.Key, group => group.Count())
                .OrderBy(pair => pair.Key, StringComparer.Ordinal),
                delta.Subcommands.OrderBy(pair => pair.Key, StringComparer.Ordinal));
        }

        public void Dispose()
        {
            if (!closed) window.Close();
        }
    }
}
