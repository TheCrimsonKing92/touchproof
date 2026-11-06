using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Infrastructure;

// Owned repositories and reader handles. The only global swap forwards all observations to the
// installed census, captures only this test's launch identity, and is restored after child exit.
public sealed class GitProcessCompletionObserverTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("git-completion-").FullName;

    [Fact]
    public void Observer_RealGitCommands_ReportsOneTimedObservationPerProcess()
    {
        using var capture = new Capture();
        var environment = GitEnvironment();
        Succeeded(GitCli.RunWithEnvironment(root, environment, "init"));
        Succeeded(GitCli.RunWithEnvironment(root, environment, "commit", "--allow-empty", "-m", "seed"));
        Succeeded(GitCli.RunWithEnvironment(root, environment, "status", "--porcelain"));
        var failed = GitCli.Run(root, "no-such-completion-command");
        Assert.True(failed.ProcessStarted);
        Assert.False(failed.Succeeded);
        var missing = GitCli.RunExecutable(Path.Combine(root, "missing-git.exe"), root, 60_000, "ignored");
        Assert.False(missing.ProcessStarted);

        var observations = capture.Observations.ToArray();
        Assert.Equal(new[] { "init", "commit", "status", "no-such-completion-command" },
            observations.Select(value => value.Subcommand));
        Assert.All(observations, AssertTiming);
    }

    [Fact]
    public void ReaderBatchProcess_Exit_ContributesOneObservation()
    {
        var environment = GitEnvironment();
        Succeeded(GitCli.RunWithEnvironment(root, environment, "init"));
        Succeeded(GitCli.RunWithEnvironment(root, environment, "commit", "--allow-empty", "-m", "seed"));
        using var capture = new Capture();
        using var reader = new GitRevisionReader(clock: new CountingClock(), admissionReads: 0);
        string? expected = null;
        for (var index = 0; index < 50; index++)
        {
            // A peel bypasses the ref-file resolver so this exercises the batch process.
            Assert.True(reader.TryResolve(root, "HEAD^{commit}", out var actual));
            expected ??= actual;
            Assert.Equal(expected, actual);
        }
        var batch = reader.GetReader(root);
        Assert.NotNull(batch);
        Assert.False(batch.HasExited);
        Assert.DoesNotContain(capture.Observations, timing => timing.Subcommand == "cat-file");

        reader.Dispose();
        Assert.True(batch.HasExited);
        var observation = Assert.Single(capture.Observations.Where(timing => timing.Subcommand == "cat-file"));
        AssertTiming(observation);
        batch.KillProcess();
        reader.Dispose();
        Assert.Single(capture.Observations.Where(timing => timing.Subcommand == "cat-file"));
    }

    [Fact]
    public void NoObserver_LaunchPath_ReadsNoClockAndCreatesNoObservation()
    {
        var clock = new CountingClock();
        Succeeded(GitCli.RunExecutableObserved("git", root, 60_000, null, clock, "--version"));
        Assert.Equal(0, clock.Reads);
        var observations = new List<GitCli.GitProcessTiming>();
        Succeeded(GitCli.RunExecutableObserved("git", root, 60_000, _ => observations.Add, clock, "--version"));
        Assert.Equal(3, clock.Reads);
        var observation = Assert.Single(observations);
        Assert.Equal(1, observation.StartMs);
        Assert.Equal(2, observation.WallMs);
    }

    [Fact]
    public void Completion_UnreadableCpu_ReportsUnavailableOnce()
    {
        var observations = new List<GitCli.GitProcessTiming>();
        var completion = GitCli.ProcessCompletion.Begin(_ => observations.Add, "status", new CountingClock());
        Assert.NotNull(completion);
        completion.Started();
        using var unavailableProcess = new System.Diagnostics.Process();
        unavailableProcess.Dispose();
        completion.Complete(unavailableProcess);
        completion.Complete(unavailableProcess);
        Assert.Null(Assert.Single(observations).CpuMs);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Completion_ThrowingCallback_PreservesGitResult(bool throwAtCompletion)
    {
        var baseline = GitCli.RunExecutableObserved("git", root, 60_000, null, TimeProvider.System, "--version");
        Succeeded(baseline);
        var calls = 0;
        var actual = GitCli.RunExecutableObserved("git", root, 60_000, _ =>
        {
            if (!throwAtCompletion)
            {
                calls++;
                throw new InvalidOperationException("observer factory failure");
            }
            return _ => { calls++; throw new InvalidOperationException("completion sink failure"); };
        }, TimeProvider.System, "--version");
        Assert.Equal(1, calls);
        Assert.Equal(baseline, actual);
    }

    private Dictionary<string, string> GitEnvironment() => new()
    {
        ["GIT_AUTHOR_NAME"] = "Timing", ["GIT_AUTHOR_EMAIL"] = "timing@example.test",
        ["GIT_COMMITTER_NAME"] = "Timing", ["GIT_COMMITTER_EMAIL"] = "timing@example.test",
        ["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null",
        ["GIT_CONFIG_NOSYSTEM"] = "1", ["GIT_CONFIG_COUNT"] = "2",
        ["GIT_CONFIG_KEY_0"] = "commit.gpgSign", ["GIT_CONFIG_VALUE_0"] = "false",
        ["GIT_CONFIG_KEY_1"] = "core.hooksPath", ["GIT_CONFIG_VALUE_1"] = Path.Combine(root, "no-hooks")
    };

    private static void AssertTiming(GitCli.GitProcessTiming timing)
    {
        Assert.True(double.IsFinite(timing.StartMs) && timing.StartMs >= 0);
        Assert.True(double.IsFinite(timing.WallMs) && timing.WallMs >= timing.StartMs);
        if (timing.CpuMs is { } cpu) Assert.True(double.IsFinite(cpu) && cpu >= 0);
        else Assert.Null(timing.CpuMs);
    }

    private static void Succeeded(GitCli.GitResult result) => Assert.True(result.Succeeded, result.Error);

    private sealed class CountingClock : TimeProvider
    {
        internal int Reads;
        public override long TimestampFrequency => 1_000;
        public override long GetTimestamp() => Interlocked.Increment(ref Reads);
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.UnixEpoch;
    }

    private sealed class Capture : IDisposable
    {
        private readonly Func<string, Action<GitCli.GitProcessTiming>?>? previous;
        private readonly Func<string, Action<GitCli.GitProcessTiming>?> observer;
        internal ConcurrentQueue<GitCli.GitProcessTiming> Observations { get; } = new();

        internal Capture()
        {
            HostCapacitySlotGitCensus.EnsureInstalled("completion-observer-tests");
            var identity = HostCapacitySlotTimingWriter.CurrentTestName();
            Assert.NotEqual("<unknown>", identity);
            previous = Volatile.Read(ref GitCli.CompletionObserver);
            observer = command =>
            {
                var sink = previous?.Invoke(command);
                if (HostCapacitySlotTimingWriter.CurrentTestName() != identity) return sink;
                return timing =>
                {
                    Observations.Enqueue(timing);
                    sink?.Invoke(timing);
                };
            };
            Assert.Same(previous, Interlocked.CompareExchange(ref GitCli.CompletionObserver, observer, previous));
        }

        public void Dispose() =>
            Assert.Same(observer, Interlocked.CompareExchange(ref GitCli.CompletionObserver, previous, observer));
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
