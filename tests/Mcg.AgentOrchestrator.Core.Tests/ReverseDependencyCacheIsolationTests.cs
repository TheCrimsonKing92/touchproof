using Mcg.AgentOrchestrator.Core;
using Repository = ReverseDependencyIndexScopeTests.Repository;

public sealed class ReverseDependencyCacheIsolationTests
{
    [Fact]
    public void Read_DefaultCacheClearAndOverfill_PreservesPrivateHitAndEvictsOldestDefaultEntry()
    {
        using var privateRepository = Repository.Create();
        using var oldestDefaultRepository = Repository.Create();
        var privateCache = new ReverseDependencyIndexCache();
        var baseline = Assert.IsType<ReverseDependencyCacheReceipt>(
            ReverseDependencyTestImpactReader.Read(privateRepository.Root, [Repository.CoreSource], cache: privateCache)
                .CacheReceipt);
        Assert.Equal(ReverseDependencyCacheDisposition.Miss, baseline.Disposition);

        ReverseDependencyTestImpactReader.ClearCacheForTests();
        var oldestDefault = AssertDefaultCacheMiss(oldestDefaultRepository);
        // Each temporary root contributes a distinct fingerprint.
        for (var i = 0; i < 4; i++)
        {
            using var otherDefaultRepository = Repository.Create();
            AssertDefaultCacheMiss(otherDefaultRepository);
        }

        var revisitedPrivate = Assert.IsType<ReverseDependencyCacheReceipt>(
            ReverseDependencyTestImpactReader.Read(privateRepository.Root, [Repository.CoreSource], cache: privateCache)
                .CacheReceipt);
        Assert.Equal(baseline.Fingerprint, revisitedPrivate.Fingerprint);
        Assert.Equal(ReverseDependencyCacheDisposition.Hit, revisitedPrivate.Disposition);
        Assert.Equal(1, revisitedPrivate.RetainedSnapshotCount);

        var revisitedDefault = AssertDefaultCacheMiss(oldestDefaultRepository);
        Assert.Equal(oldestDefault.Fingerprint, revisitedDefault.Fingerprint);
    }

    private static ReverseDependencyCacheReceipt AssertDefaultCacheMiss(Repository repository)
    {
        var receipt = Assert.IsType<ReverseDependencyCacheReceipt>(
            ReverseDependencyTestImpactReader.Read(repository.Root, [Repository.CoreSource]).CacheReceipt);
        Assert.Equal(ReverseDependencyCacheDisposition.Miss, receipt.Disposition);
        Assert.InRange(receipt.RetainedSnapshotCount, 1, 4);
        return receipt;
    }
}
