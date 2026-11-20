using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;

public sealed class DependencyIndexCacheTests
{
    [Fact]
    public void Process_cache_reuses_one_file_backed_index_for_the_App_assembly()
    {
        var app = typeof(GoalMonitoringQuery).Assembly;
        var index = DependencyIndexCache.Get(app);

        Assert.Same(index, DependencyIndexCache.Get(typeof(BoardFillCliGoalIntake).Assembly));
        Assert.Same(index, DependencyIndexCache.Get(typeof(AttentionAnswerHandlerAdapter).Assembly));
        Assert.Same(index, DependencyIndexCache.Get(typeof(OrphanFixtureReaper).Assembly));
        Assert.Equal(1, DependencyIndexCache.Process.BuildCount(app));
    }

    [Fact]
    public async Task Concurrent_callers_build_one_index_and_reuse_its_instance()
    {
        const int callers = 12;
        var image = DependencyIndexFixtureAssembly.Compile("internal sealed class Subject { }");
        using var ready = new CountdownEvent(callers);
        using var start = new ManualResetEventSlim();
        using var entered = new CountdownEvent(callers);
        var builds = 0;
        var cache = new DependencyIndexCache(types =>
        {
            Interlocked.Increment(ref builds);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(30)), "A caller did not reach the cache.");
            return new DependencyIndex(types);
        });

        var requests = Enumerable.Range(0, callers).Select(_ => Task.Factory.StartNew(() =>
        {
            ready.Signal();
            Assert.True(start.Wait(TimeSpan.FromSeconds(30)), "The caller start signal did not arrive.");
            entered.Signal();
            return cache.GetOrBuildImages(image);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();

        var allReady = ready.Wait(TimeSpan.FromSeconds(30));
        start.Set();
        var results = await Task.WhenAll(requests);
        Assert.True(allReady, "A caller did not reach the start barrier.");
        Assert.Equal(1, builds);
        Assert.All(results, index => Assert.Same(results[0], index));
        Assert.Same(cache.GetOrBuildImages(image), cache.GetOrBuildImages(image));
        Assert.Equal(1, builds);
    }

    [Fact]
    public void Ordered_set_identity_uses_contents_and_deduplicates_repeated_inputs()
    {
        var first = DependencyIndexFixtureAssembly.Compile("internal sealed class First { }", "Fixture");
        var second = DependencyIndexFixtureAssembly.Compile("internal sealed class Second { }", "Fixture");
        var cache = DependencyIndexCache.Process;

        Assert.Same(cache.GetOrBuildImages(first), cache.GetOrBuildImages(first.ToArray(), first));
        Assert.NotSame(cache.GetOrBuildImages(first), cache.GetOrBuildImages(second));

        var original = cache.GetOrBuildImages(first, second);
        var reversed = cache.GetOrBuildImages(second, first);
        Assert.NotSame(original, reversed);
        Assert.Equal(new[] { "First", "Second" }, original.Types.Select(type => type.FullName));
        Assert.Equal(new[] { "Second", "First" }, reversed.Types.Select(type => type.FullName));

        Array.Clear(first);
        Assert.Equal("First", original.Types[0].FullName);
    }
}
