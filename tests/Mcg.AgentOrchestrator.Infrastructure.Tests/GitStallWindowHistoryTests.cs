using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: histories, clocks and temporary directories are owned by each test.
public sealed class GitStallWindowHistoryTests
{
    [Fact]
    public void Window_RecordedIdleGaps_UsesOwnLongestThenGlobalFallback()
    {
        var history = new GitIdleGapHistory();
        Assert.Null(history.Window("worktree add"));
        history.Record("status", TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.FromSeconds(90), history.Window("worktree add"));
        history.Record("worktree add", TimeSpan.FromSeconds(35));
        history.Record("worktree add", TimeSpan.FromSeconds(30));
        Assert.Equal(TimeSpan.FromSeconds(70), history.Window("worktree add"));
        history.Record("worktree add", TimeSpan.FromSeconds(50));
        Assert.Equal(TimeSpan.FromSeconds(100), history.Window("worktree add"));
        Assert.Equal(TimeSpan.FromSeconds(100), history.Window("log"));
    }

    [Fact]
    public void Window_SubSampleGaps_StayEmptyAndDoNotHideGlobalEvidence()
    {
        var history = new GitIdleGapHistory();
        history.Record("status", TimeSpan.Zero);
        history.Record("status", TimeSpan.FromMilliseconds(50));
        Assert.Null(history.Window("status"));
        Assert.Null(history.Window("log"));
        history.Record("log", TimeSpan.FromSeconds(45));
        Assert.Equal(TimeSpan.FromSeconds(90), history.Window("status"));
    }

    [Fact]
    public void Window_OneSamplingInterval_EnablesTheMinimum()
    {
        var history = new GitIdleGapHistory();
        history.Record("status", GitProgressWatch.SamplingInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), history.Window("status"));
    }

    [Fact]
    public void Record_FileBackedHistory_RoundTripsRawGapsAndMergesStaleWriters()
    {
        var root = Directory.CreateTempSubdirectory("git-idle-history-").FullName;
        try
        {
            var path = Path.Combine(root, GitIdleGapHistory.FileName);
            var first = new GitIdleGapHistory(path);
            var stale = new GitIdleGapHistory(path);
            Assert.Null(first.Window("status"));
            Assert.False(File.Exists(path));
            first.Record("status", TimeSpan.FromSeconds(45));
            Assert.True(File.Exists(path));
            using (var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path)))
            {
                Assert.Equal(45000, json.RootElement.GetProperty("longestIdleGapMilliseconds").GetDouble());
                Assert.Equal(45000, json.RootElement.GetProperty("subcommands").GetProperty("status").GetDouble());
            }
            var second = new GitIdleGapHistory(path);
            Assert.Equal(first.Window("status"), second.Window("status"));
            Assert.Equal(TimeSpan.FromSeconds(90), second.Window("log"));

            stale.Record("log", TimeSpan.FromSeconds(35));
            first.Record("status", TimeSpan.FromSeconds(50));
            var merged = new GitIdleGapHistory(path);
            Assert.Equal(TimeSpan.FromSeconds(100), merged.Window("status"));
            Assert.Equal(TimeSpan.FromSeconds(70), merged.Window("log"));
            Assert.Equal(TimeSpan.FromSeconds(100), merged.Window("diff"));

            // An unchanged maximum must not attempt another save, even if the file disappeared.
            File.Delete(path);
            first.Record("status", TimeSpan.FromSeconds(49));
            Assert.False(File.Exists(path));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"version\":1,\"subcommands\":{\"status\":45000}}")]
    [InlineData("{\"version\":1,\"longestIdleGapMilliseconds\":-1,\"subcommands\":{\"status\":-1}}")]
    [InlineData("{\"version\":1,\"longestIdleGapMilliseconds\":45000,\"subcommands\":{\"status\":\"bad\"}}")]
    [InlineData("{\"version\":2,\"longestIdleGapMilliseconds\":45000,\"subcommands\":{\"status\":45000}}")]
    [InlineData("{\"version\":1,\"longestIdleGapMilliseconds\":0,\"subcommands\":{\"status\":45000}}")]
    public void Window_MalformedFile_StartsEmptyAndCanRecord(string content)
    {
        var root = Directory.CreateTempSubdirectory("git-idle-malformed-").FullName;
        try
        {
            var path = Path.Combine(root, GitIdleGapHistory.FileName);
            File.WriteAllText(path, content);
            var history = new GitIdleGapHistory(path);
            Assert.Null(history.Window("status"));
            Assert.Null(history.Window("log"));
            history.Record("status", TimeSpan.FromSeconds(45));
            Assert.Equal(TimeSpan.FromSeconds(90), new GitIdleGapHistory(path).Window("status"));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void Record_UnreadableFile_KeepsInMemoryEvidenceWithoutThrowing()
    {
        var root = Directory.CreateTempSubdirectory("git-idle-unreadable-").FullName;
        try
        {
            var history = new GitIdleGapHistory(root); // A directory cannot be read or replaced as a file.
            Assert.Null(history.Window("status"));
            history.Record("status", TimeSpan.FromSeconds(45));
            Assert.Equal(TimeSpan.FromSeconds(90), history.Window("status"));
            Assert.Empty(Directory.GetFiles(root));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public void Wait_FastRunThenTwoSecondsIdle_PreservesNaturalExit()
    {
        var history = new GitIdleGapHistory();
        var fast = new GitProgressWatchTests.FakeChild(1, (child, _) => child.OutputBytes++);
        var finished = GitProgressWatchTests.Watch(fast, history.Window("worktree add"));
        Assert.False(finished.Stalled);
        Assert.Equal(TimeSpan.FromMilliseconds(100), fast.Clock.Elapsed);
        Assert.Equal(TimeSpan.Zero, finished.LongestIdle);
        history.Record("worktree add", finished.LongestIdle);
        Assert.Null(history.Window("worktree add"));

        var idle = new GitProgressWatchTests.FakeChild(20);
        var result = GitProgressWatchTests.Watch(idle, history.Window("worktree add"));
        Assert.False(result.Stalled);
        Assert.Equal(0, idle.Kills);
        Assert.Equal(20, result.Last.Samples);
        Assert.Equal(TimeSpan.FromSeconds(2), result.LongestIdle);
    }

    [Theory]
    [InlineData(700, true, 601)]
    [InlineData(500, false, 500)]
    public void Wait_FiveSecondHistory_EnforcesMinimumIdleWindow(int steps, bool stalled, int samples)
    {
        var history = new GitIdleGapHistory();
        history.Record("worktree add", TimeSpan.FromSeconds(5));
        Assert.Equal(TimeSpan.FromSeconds(60), history.Window("worktree add"));
        var child = new GitProgressWatchTests.FakeChild(steps);
        var result = GitProgressWatchTests.Watch(child, history.Window("worktree add"));
        Assert.Equal(stalled, result.Stalled);
        Assert.Equal(stalled ? 1 : 0, child.Kills);
        Assert.Equal(samples, result.Last.Samples);
        Assert.Equal(TimeSpan.FromMilliseconds(samples * 100), result.LongestIdle);
    }

    [Fact]
    public void Wait_EmptyHistory_MonitorsIdlePastSixtySecondsAndReportsObservations()
    {
        var child = new GitProgressWatchTests.FakeChild(700);
        var reports = new List<string>();
        var reportSamples = new List<long>();
        var result = GitProgressWatchTests.Watch(child, new GitIdleGapHistory().Window("worktree add"),
            reports.Add, observation =>
            {
                if ((observation.Samples & (observation.Samples - 1)) == 0)
                    reportSamples.Add(observation.Samples);
            });
        Assert.False(result.Stalled);
        Assert.Equal(0, child.Kills);
        Assert.Equal(700, result.Last.Samples);
        Assert.True(child.Clock.Elapsed > TimeSpan.FromSeconds(60));
        Assert.Equal(new long[] { 1, 2, 4, 8, 16, 32, 64, 128, 256, 512 }, reportSamples);
        Assert.Equal(reportSamples.Count, reports.Count);
        for (var index = 0; index < reports.Count; index++)
        {
            Assert.Contains($"samples={reportSamples[index]};", reports[index]);
            Assert.Contains("processorTimeMs=0", reports[index]);
            Assert.Contains("stdoutBytes=0", reports[index]);
            Assert.Contains("stderrBytes=0", reports[index]);
            Assert.Contains("starvedSamples=0", reports[index]);
            Assert.Contains("stallWindowMs=none", reports[index]);
        }
    }

    [Fact]
    public void Wait_LaunchSnapshot_StaysFixedUntilTheNextLaunch()
    {
        var history = new GitIdleGapHistory();
        history.Record("worktree add", TimeSpan.FromSeconds(40));
        var child = new GitProgressWatchTests.FakeChild(900);
        var result = GitProgressWatchTests.Watch(child, history.Window("worktree add"),
            sampled: _ => history.Record("worktree add", TimeSpan.FromSeconds(100)));
        Assert.True(result.Stalled);
        Assert.Equal(TimeSpan.FromSeconds(80), result.StallWindow);
        var next = GitProgressWatchTests.Watch(new(10), history.Window("worktree add"));
        Assert.False(next.Stalled);
        Assert.Equal(TimeSpan.FromSeconds(200), next.StallWindow);
    }

    [Theory]
    [InlineData("worktree add", new[] { "-c", "x=y", "-C", "a", "--git-dir=b", "worktree", "add", "path", "HEAD" })]
    [InlineData("worktree remove", new[] { "--no-pager", "worktree", "remove", "path" })]
    [InlineData("status", new[] { "--work-tree", "path", "status", "--porcelain", "--", "path" })]
    [InlineData("log", new[] { "log", "HEAD", "--", "path" })]
    [InlineData("stash", new[] { "stash", "topic-name" })]
    [InlineData("notes show", new[] { "notes", "--ref", "refs/notes/topic", "show", "HEAD" })]
    public void SubcommandKey_GlobalOptionsAndOperands_AreExcluded(string expected, string[] args) =>
        Assert.Equal(expected, GitCli.SubcommandKey(args));
}
