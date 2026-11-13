using System.Runtime.CompilerServices;
using Mcg.AgentOrchestrator.Infrastructure;

// Hook swaps forward all existing instrumentation; probes are synchronous and test-identity scoped.
public sealed class GitLaunchWindowCensusTests
{
    [Fact]
    public async Task FlowWindows_ExcludeUnrelatedWorkAndIncludeInheritedWork()
    {
        using var census = new GitLaunchWindowCensus();
        using var window = census.OpenWindow("tick");
        Task unrelated;
        using (ExecutionContext.SuppressFlow())
            unrelated = Task.Run(() =>
            {
                census.RecordLaunch("status", "Unrelated.Read");
                Assert.Null(census.BeginTiming("status", "Unrelated.Read"));
            });
        await unrelated;
        await Task.Run(() =>
        {
            var sink = census.BeginTiming("status", "Inherited.Read");
            census.RecordLaunch("status", "Inherited.Read");
            Assert.NotNull(sink);
            sink(new("status", 0, 17, null));
        });
        var totals = window.Close();
        Assert.Equal(new GitLaunchWindowCensus.Measure(1, 1, 17), totals.Total);
        Assert.Equal("Inherited.Read", Assert.Single(totals.CallSites).Key);
    }

    [Fact]
    public void SequentialWindows_KeepCountsCallersAndDurationsSeparate()
    {
        HostCapacitySlotGitCensus.EnsureInstalled("window-census-tests");
        using var census = GitLaunchWindowCensus.Install();
        RaiseRead("outside", 100);
        using var first = census.OpenWindow("gate");
        RaiseRead("status", 4);
        RaiseRead("status", 6);
        RaiseWrite("update-ref", 15);
        var late = GitCli.CompletionObserver!("status");
        var a = first.Close();
        using var second = census.OpenWindow("tick");
        late?.Invoke(new("status", 0, 500, null));
        RaiseWrite("update-ref", 9);
        var b = second.Close();

        Assert.Equal("gate", a.Name);
        Assert.Equal(new GitLaunchWindowCensus.Measure(3, 3, 25), a.Total);
        Assert.Equal(new GitLaunchWindowCensus.Measure(2, 2, 10), a.Subcommands["status"]);
        Assert.Equal(new GitLaunchWindowCensus.Measure(1, 1, 15), a.Subcommands["update-ref"]);
        Assert.Equal(new GitLaunchWindowCensus.Measure(2, 2, 10), a.CallSites["GitLaunchWindowCensusTests.RaiseRead"]);
        Assert.Equal(new GitLaunchWindowCensus.Measure(1, 1, 15), a.CallSites["GitLaunchWindowCensusTests.RaiseWrite"]);
        Assert.Equal(2, a.Subcommands.Count);
        Assert.Equal(2, a.CallSites.Count);
        Assert.Equal("tick", b.Name);
        Assert.Equal(new GitLaunchWindowCensus.Measure(1, 1, 9), b.Total);
        Assert.Equal(new GitLaunchWindowCensus.Measure(1, 1, 9), Assert.Single(b.Subcommands).Value);
        Assert.Equal("update-ref", Assert.Single(b.Subcommands).Key);
        Assert.Equal("GitLaunchWindowCensusTests.RaiseWrite", Assert.Single(b.CallSites).Key);
        Assert.Equal(new GitLaunchWindowCensus.Measure(1, 1, 9), Assert.Single(b.CallSites).Value);
        var detail = GitLaunchWindowCensus.FormatTick(1, 20, b);
        Assert.Contains("git_subcommands=update-ref:1", detail);
        Assert.True(GitLaunchWindowCensus.TryParseTick(detail, out var tick));
        Assert.NotNull(tick);
        Assert.Equal("update-ref", Assert.Single(tick.Subcommands).Key);
        Assert.Equal(1, Assert.Single(tick.Subcommands).Value);
        Assert.Equal(1, tick.Launches);
        Assert.Equal(9, tick.WallMs);
        using var empty = census.OpenWindow("empty");
        Assert.Equal(new GitLaunchWindowCensus.Measure(0, 0, 0), empty.Close().Total);
        Assert.Empty(empty.Close().CallSites);
    }

    [Fact]
    public void InstalledObservers_ReceiveEveryProbeAndAreRestoredOnDispose()
    {
        using var prior = new PriorHooks();
        using var census = GitLaunchWindowCensus.Install();
        using var window = census.OpenWindow("tick");
        RaiseRead("status", 7);
        RaiseWrite("update-ref", 11);
        census.Dispose();
        Assert.Same(prior.Launch, GitCli.LaunchObserver);
        Assert.Same(prior.Completion, GitCli.CompletionObserver);
        RaiseRead("status", 3);
        Assert.Equal(3, prior.Launches);
        Assert.Equal(3, prior.Completions);
        Assert.Equal(21, prior.WallMs);
        Assert.Equal(new GitLaunchWindowCensus.Measure(2, 2, 18), window.Close().Total);
    }

    [Fact]
    public void StackedObserver_DisposeKeepsSuccessorAndBecomesPassThrough()
    {
        using var prior = new PriorHooks();
        using var first = GitLaunchWindowCensus.Install();
        var firstLaunch = GitCli.LaunchObserver;
        var firstCompletion = GitCli.CompletionObserver;
        using var window = first.OpenWindow("first");
        using var successor = GitLaunchWindowCensus.Install();
        var launch = GitCli.LaunchObserver;
        var completion = GitCli.CompletionObserver;
        first.Dispose();
        Assert.Same(launch, GitCli.LaunchObserver);
        Assert.Same(completion, GitCli.CompletionObserver);
        using var next = successor.OpenWindow("next");
        RaiseRead("status", 5);
        Assert.Equal(new GitLaunchWindowCensus.Measure(1, 1, 5), next.Close().Total);
        Assert.Equal(new GitLaunchWindowCensus.Measure(0, 0, 0), window.Close().Total);
        Assert.Equal(1, prior.Launches);
        Assert.Equal(1, prior.Completions);
        // Unwind the disposed pass-through before restoring this test's prior observer.
        successor.Dispose();
        Assert.Same(firstLaunch, Interlocked.CompareExchange(ref GitCli.LaunchObserver, prior.Launch, firstLaunch));
        Assert.Same(firstCompletion, Interlocked.CompareExchange(ref GitCli.CompletionObserver, prior.Completion, firstCompletion));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RaiseRead(string command, double wall)
    {
        var sink = GitCli.CompletionObserver?.Invoke(command);
        GitCli.LaunchObserver?.Invoke(command);
        sink?.Invoke(new(command, 0, wall, null));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RaiseWrite(string command, double wall)
    {
        var sink = GitCli.CompletionObserver?.Invoke(command);
        GitCli.LaunchObserver?.Invoke(command);
        sink?.Invoke(new(command, 0, wall, null));
    }

    private sealed class PriorHooks : IDisposable
    {
        private readonly Action<string>? previousLaunch;
        private readonly Func<string, Action<GitCli.GitProcessTiming>?>? previousCompletion;
        internal readonly Action<string> Launch;
        internal readonly Func<string, Action<GitCli.GitProcessTiming>?> Completion;
        internal int Launches, Completions;
        internal double WallMs;

        internal PriorHooks()
        {
            HostCapacitySlotGitCensus.EnsureInstalled("window-census-tests");
            var identity = HostCapacitySlotTimingWriter.CurrentTestName();
            Assert.NotEqual("<unknown>", identity);
            previousLaunch = Volatile.Read(ref GitCli.LaunchObserver);
            previousCompletion = Volatile.Read(ref GitCli.CompletionObserver);
            Launch = command =>
            {
                if (HostCapacitySlotTimingWriter.CurrentTestName() == identity) Launches++;
                previousLaunch?.Invoke(command);
            };
            Completion = command =>
            {
                var other = previousCompletion?.Invoke(command);
                if (HostCapacitySlotTimingWriter.CurrentTestName() != identity) return other;
                return timing => { Completions++; WallMs += timing.WallMs; other?.Invoke(timing); };
            };
            Assert.Same(previousLaunch, Interlocked.CompareExchange(ref GitCli.LaunchObserver, Launch, previousLaunch));
            Assert.Same(previousCompletion, Interlocked.CompareExchange(ref GitCli.CompletionObserver, Completion, previousCompletion));
        }

        public void Dispose()
        {
            Assert.Same(Launch, Interlocked.CompareExchange(ref GitCli.LaunchObserver, previousLaunch, Launch));
            Assert.Same(Completion, Interlocked.CompareExchange(ref GitCli.CompletionObserver, previousCompletion, Completion));
        }
    }
}
