using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class PreTesterReverseDependencySelectionSourceHandoffTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LaneScanReusesWarmAndStoredExtractsWithHandoff(bool persisted)
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        fixture.WithCostHistory("""
            , "laneTestSourceRoots": ["tests/Mcg.AgentOrchestrator.Infrastructure.Tests"]
            """);
        var store = persisted ? Path.Combine(fixture.Root, "extract-store") : null;
        var first = new AcceptanceTestClassSourceCache(store);
        using var firstScope = AcceptanceTestClassSourceCache.Use(first);
        var expected = AcceptanceTestClassSourceScanner.Scan(fixture.Root, new SelectionSourceTexts());
        Assert.Equal("ConsumerTests", Assert.Single(expected).FullName);
        Assert.Equal(1, first.ParsedFileCount);
        var next = persisted ? new AcceptanceTestClassSourceCache(store) : first;
        using var nextScope = AcceptanceTestClassSourceCache.Use(next);
        var reads = new ConcurrentQueue<string>();
        var actual = SelectionFileReadProbe.Observe((_, path, _) => reads.Enqueue(path), () =>
            AcceptanceTestClassSourceScanner.Scan(fixture.Root, new SelectionSourceTexts()));
        Assert.Equal(expected, actual);
        Assert.Equal(persisted ? 0 : 1, next.ParsedFileCount);
        Assert.Equal(persisted ? 3 : 1, reads.Count);
        Assert.Single(reads, path => path.EndsWith("acceptance-manifest.json", StringComparison.Ordinal));
        if (persisted)
        {
            Assert.Single(reads, path => path.EndsWith(".cs", StringComparison.Ordinal));
            Assert.Single(reads, path => path.StartsWith(store! + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        }
        Assert.Equal(1, next.CachedPathsForTests(fixture.Root).Count);
    }

    [Fact]
    public void EarlierHandoffTextCannotPoisonLaterExtractCacheVersion()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var path = Path.Combine(fixture.Root, PreTesterReverseDependencySelectionFixture.TestRoot + "ConsumerTests.cs");
        var handoff = new SelectionSourceTexts();
        handoff.Read(path);
        File.WriteAllText(path, "public class UpdatedConsumerTests { [Fact] public void Works() {} }");
        var cache = new AcceptanceTestClassSourceCache(null);
        var earlier = Assert.Single(cache.Extract(fixture.Root, [path], sourceTexts: handoff));
        Assert.Equal("ConsumerTests", Assert.Single(earlier.Classes).FullName);
        var current = Assert.Single(cache.Extract(fixture.Root, [path]));
        Assert.Equal("UpdatedConsumerTests", Assert.Single(current.Classes).FullName);
        Assert.Equal(2, cache.ParsedFileCount);
    }

    [Fact]
    public void EarlierHandoffDeclarationsCannotPoisonProjectCache()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var path = Path.Combine(fixture.Root, PreTesterReverseDependencySelectionFixture.TestRoot + "ConsumerTests.cs");
        var handoff = new SelectionSourceTexts();
        handoff.Read(path);
        File.WriteAllText(path, "public class UpdatedConsumerTests { [Fact] public void Works() {} }");
        var earlier = new FileSystemTestClassDeclarationReader(fixture.Root, sourceTexts: handoff)
            .ReadProject(PreTesterReverseDependencySelectionFixture.TestRoot);
        Assert.Equal("ConsumerTests", Assert.Single(earlier.ClassNames));
        var current = new FileSystemTestClassDeclarationReader(fixture.Root)
            .ReadProject(PreTesterReverseDependencySelectionFixture.TestRoot);
        Assert.Equal("UpdatedConsumerTests", Assert.Single(current.ClassNames));
    }

    [Fact]
    public async Task ConcurrentReadersShareOnePhysicalOpenAcrossPathAliases()
    {
        using var fixture = new PreTesterReverseDependencySelectionFixture();
        var path = Path.Combine(fixture.Root, PreTesterReverseDependencySelectionFixture.TestRoot + "ConsumerTests.cs");
        var expected = File.ReadAllText(path);
        var handoff = new SelectionSourceTexts();
        var reads = new ConcurrentQueue<string>();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arrivals = 0;
        var running = SelectionFileReadProbe.Observe((_, file, _) => reads.Enqueue(file), () =>
            Enumerable.Range(0, 8).Select(index => Task.Run(async () =>
            {
                if (Interlocked.Increment(ref arrivals) == 8) ready.SetResult();
                await start.Task;
                var alias = index % 2 == 0 ? path : Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path));
                if (OperatingSystem.IsWindows() && index % 3 == 0) alias = alias.ToUpperInvariant();
                return handoff.Read(alias);
            })).ToArray());
        try { await TestHangGuard.WaitAsync(ready.Task, "all handoff readers reached the start gate"); }
        finally
        {
            start.TrySetResult();
            await TestHangGuard.WaitAsync(Task.WhenAll(running), "concurrent handoff reads completed");
        }
        var results = await Task.WhenAll(running);
        Assert.All(results, text => Assert.Equal(expected, text));
        Assert.Single(reads);
    }
}
