using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: histories and clocks are instance-owned; no processes or global hooks.
public sealed class GitStallWindowHistoryTests
{
    [Fact]
    public void Window_RecordedDurations_UsesOwnLongestThenGlobalFallback()
    {
        var history = new GitCli.DurationHistory();
        Assert.Null(history.Window("worktree add"));
        history.Record("status", TimeSpan.FromMilliseconds(300));
        Assert.Equal(TimeSpan.FromMilliseconds(300), history.Window("worktree add"));
        history.Record("worktree add", TimeSpan.FromMilliseconds(100));
        history.Record("worktree add", TimeSpan.FromMilliseconds(90));
        Assert.Equal(TimeSpan.FromMilliseconds(100), history.Window("worktree add"));
        history.Record("worktree add", TimeSpan.FromMilliseconds(500));
        Assert.Equal(TimeSpan.FromMilliseconds(500), history.Window("worktree add"));
        Assert.Equal(TimeSpan.FromMilliseconds(500), history.Window("log"));
    }

    [Fact]
    public void Wait_EmptyHistory_MonitorsIdlePastSixtySecondsAndReportsObservations()
    {
        var child = new GitProgressWatchTests.FakeChild(700);
        var reports = new List<string>();
        var reportSamples = new List<long>();
        var result = GitProgressWatchTests.Watch(child, new GitCli.DurationHistory().Window("worktree add"),
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
        var history = new GitCli.DurationHistory();
        history.Record("worktree add", TimeSpan.FromMilliseconds(200));
        var child = new GitProgressWatchTests.FakeChild(10);
        var result = GitProgressWatchTests.Watch(child, history.Window("worktree add"),
            sampled: _ => history.Record("worktree add", TimeSpan.FromSeconds(10)));
        Assert.True(result.Stalled);
        Assert.Equal(TimeSpan.FromMilliseconds(200), result.StallWindow);
        var next = GitProgressWatchTests.Watch(new(10), history.Window("worktree add"));
        Assert.False(next.Stalled);
        Assert.Equal(TimeSpan.FromSeconds(10), next.StallWindow);
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
