using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;
using static GitProgressWatchTests;

// Parallel-safe: only this class replaces the sink; xUnit serializes its facts. Other git
// callers may report concurrently, so callbacks filter unique tokens and use concurrent queues.
// Console capture is async-context-local; every sink replacement is restored in finally.
public sealed class GitProgressSinkTests
{
    [Fact]
    public void Wait_NoExplicitOutput_DeliversIdleReportsWithoutConsoleOutput()
    {
        const string command = "sink-delivery";
        var lines = new ConcurrentQueue<string>();
        Action<string> destination = line =>
        {
            if (line.StartsWith($"git {command} waiting:", StringComparison.Ordinal))
                lines.Enqueue(line);
        };
        var previous = GitProgressSink.Replace(destination);
        try
        {
            Assert.Same(destination, GitProgressSink.Callback);
            var child = new FakeChild(4);
            var captured = Capture(() => GitCli.WaitForProgress(child, command, null, child.Clock));
            Assert.Empty(captured.Error);
            Assert.Empty(captured.Output);
            Assert.NotEmpty(lines);
            Assert.All(lines, line => Assert.StartsWith($"git {command} waiting:", line));

            var expected = new List<string>();
            var baselineChild = new FakeChild(4);
            var baseline = GitCli.WaitForProgress(baselineChild, command, null,
                baselineChild.Clock, progressOutput: expected.Add);
            Assert.Equal(expected.ToArray(), lines.ToArray());
            Assert.Equal(baseline, captured.Result);
            Assert.True(child.HasExited);
            Assert.Equal(0, child.Kills);
        }
        finally { GitProgressSink.Replace(previous); }
        Assert.Same(previous, GitProgressSink.Callback);
    }

    [Fact]
    public void Wait_DefaultSink_DropsIdleReportsWithoutConsoleOutput()
    {
        var previous = GitProgressSink.Replace(null);
        try
        {
            Assert.NotNull(GitProgressSink.Callback);
            var child = new FakeChild(4);
            var captured = Capture(() => GitCli.WaitForProgress(child, "sink-default", null, child.Clock));
            Assert.Empty(captured.Error);
            Assert.Empty(captured.Output);
            Assert.False(captured.Result.Stalled);
            Assert.True(captured.Result.LongestIdle >= GitProgressWatch.SamplingInterval);
            Assert.True(child.HasExited);
            Assert.Equal(0, child.Kills);
        }
        finally { GitProgressSink.Replace(previous); }
    }

    [Fact]
    public void Wait_ThrowingSink_PreservesResultWithoutConsoleOutput()
    {
        const string command = "sink-throwing";
        var calls = 0;
        var previous = GitProgressSink.Replace(line =>
        {
            if (!line.StartsWith($"git {command} waiting:", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("sink failure");
        });
        try
        {
            var baselineChild = new FakeChild(4);
            var baseline = GitCli.WaitForProgress(baselineChild, command, null,
                baselineChild.Clock, progressOutput: static _ => { });
            var child = new FakeChild(4);
            var captured = Capture(() => GitCli.WaitForProgress(child, command, null, child.Clock));
            Assert.True(Volatile.Read(ref calls) > 0, "The throwing sink was not invoked.");
            Assert.Equal(baseline, captured.Result);
            Assert.Empty(captured.Error);
            Assert.Empty(captured.Output);
            Assert.True(child.HasExited);
            Assert.Equal(0, child.Kills);
        }
        finally { GitProgressSink.Replace(previous); }
    }

    [Fact]
    public void Wait_ExplicitOutput_UsesOnlyExplicitDestination()
    {
        const string command = "sink-explicit";
        var sinkLines = new ConcurrentQueue<string>();
        var previous = GitProgressSink.Replace(line =>
        {
            if (line.StartsWith($"git {command} waiting:", StringComparison.Ordinal))
                sinkLines.Enqueue(line);
        });
        try
        {
            var explicitLines = new List<string>();
            var child = new FakeChild(4);
            var captured = Capture(() => GitCli.WaitForProgress(child, command, null,
                child.Clock, progressOutput: explicitLines.Add));
            Assert.NotEmpty(explicitLines);
            Assert.All(explicitLines, line => Assert.StartsWith($"git {command} waiting:", line));
            Assert.Empty(sinkLines);
            Assert.Empty(captured.Error);
            Assert.Empty(captured.Output);
            Assert.False(captured.Result.Stalled);
        }
        finally { GitProgressSink.Replace(previous); }
    }

    [Fact]
    public void Wait_CallbackReplacedDuringReport_UsesNewDestinationForLaterReports()
    {
        const string command = "sink-replace";
        var firstLines = new ConcurrentQueue<string>();
        var laterLines = new ConcurrentQueue<string>();
        Action<string> later = line =>
        {
            if (line.StartsWith($"git {command} waiting:", StringComparison.Ordinal))
                laterLines.Enqueue(line);
        };
        Action<string> first = line =>
        {
            if (!line.StartsWith($"git {command} waiting:", StringComparison.Ordinal)) return;
            firstLines.Enqueue(line);
            GitProgressSink.Replace(later);
        };
        var previous = GitProgressSink.Replace(first);
        try
        {
            var child = new FakeChild(4);
            var captured = Capture(() => GitCli.WaitForProgress(child, command, null, child.Clock));
            Assert.NotEmpty(firstLines);
            Assert.NotEmpty(laterLines);
            Assert.Same(later, GitProgressSink.Callback);
            Assert.Empty(captured.Error);
            Assert.Empty(captured.Output);
            Assert.False(captured.Result.Stalled);
        }
        finally { GitProgressSink.Replace(previous); }
    }

    private static (GitProgressWatch.Result Result, string Output, string Error) Capture(
        Func<GitProgressWatch.Result> action)
    {
        GitProgressWatch.Result result = default;
        var output = string.Empty;
        var error = AsyncLocalConsoleRouter.CaptureError(() =>
        {
            output = AsyncLocalConsoleRouter.Capture(() => { result = action(); });
        });
        return (result, output, error);
    }
}
