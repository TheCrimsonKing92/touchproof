using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private caches and injected completed facts; real-git prep coverage lives in GroupedAdmissionPrepFactReuseTests.
public sealed class PreLandingMergeConflictProbeCacheTests
{
    [Fact]
    public void Capacity_EvictsLeastRecent_AndEachChangedDependencyMisses()
    {
        var key = new PreLandingMergeConflictProbeCache.Key("repo", GoalId.New(), new string('b', 40), new string('a', 40), "[]");
        var cache = new PreLandingMergeConflictProbeCache(2);
        var calls = 0;
        AdditiveConflictProbeResult Read(PreLandingMergeConflictProbeCache.Key k) => cache.GetOrProbe(() => k, () =>
        {
            calls++;
            return new(k.Branch, k.Main, AdditiveConflictProbeOutcome.Unresolvable, "frozen-path");
        });
        var second = key with { Branch = new string('c', 40) };
        var third = key with { Main = new string('d', 40) };
        var original = Read(key);
        Read(second);
        Assert.Same(original, Read(key));
        Read(third);
        Assert.Equal(2, cache.Count);
        Read(second);
        Assert.Equal(4, calls);
        foreach (var changed in new[] { key with { Repository = "other" }, key with { GoalId = GoalId.New() }, key with { FrozenPaths = "[src/Widget.cs]" } })
        {
            var before = calls;
            Read(changed);
            Assert.Equal(before + 1, calls);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IncompleteOrMovingProbe_IsNeverRetained(bool indeterminate)
    {
        var key = new PreLandingMergeConflictProbeCache.Key("repo", GoalId.New(), new string('b', 40), new string('a', 40), "[]");
        var cache = new PreLandingMergeConflictProbeCache();
        var calls = 0;
        for (var i = 0; i < 2; i++) cache.GetOrProbe(() => key, () =>
        {
            calls++;
            return new(indeterminate ? key.Branch : new string('c', 40), key.Main,
                indeterminate ? AdditiveConflictProbeOutcome.Indeterminate : AdditiveConflictProbeOutcome.Resolvable, "probe");
        });
        Assert.Equal(2, calls);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void KeyMovesDuringProbe_OldResultIsNotPublished()
    {
        var key = new PreLandingMergeConflictProbeCache.Key("repo", GoalId.New(), new string('b', 40), new string('a', 40), "[]");
        var cache = new PreLandingMergeConflictProbeCache();
        cache.GetOrProbe(() => key, () =>
        {
            var result = new AdditiveConflictProbeResult(key.Branch, key.Main, AdditiveConflictProbeOutcome.Resolvable, "none");
            key = key with { FrozenPaths = "changed" };
            return result;
        });
        Assert.Equal(0, cache.Count);
    }
}
