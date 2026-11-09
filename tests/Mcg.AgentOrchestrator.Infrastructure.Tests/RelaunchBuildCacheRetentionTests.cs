using Mcg.AgentOrchestrator.App.Orchestration;

// Parallel-safe: each test owns its cache parent and file handles; no builds or ambient changes.
public sealed class RelaunchBuildCacheRetentionTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly string root = Path.Combine(Path.GetTempPath(), "relaunch-retention-" + Guid.NewGuid().ToString("N"));
    private string CacheParent => Path.Combine(root, "caches");
    private string RepositoryRoot => Path.Combine(root, "repository");

    [Xunit.Fact]
    public void StaleCaches_OnlyUnlockedUnprotectedCacheIsEvictedAndCounted()
    {
        var stale = Cache("1111111111111111", Now.AddDays(-3), "stale payload");
        var locked = Cache("2222222222222222", Now.AddDays(-3), "locked payload");
        var current = Cache(RelaunchBuildCacheIdentity.RepositoryKey(RepositoryRoot), Now.AddDays(-30), "current payload");
        var staleLock = Path.Combine(stale, RelaunchBuildCacheIdentity.LockFileName);
        File.WriteAllText(staleLock, "");
        File.SetLastWriteTimeUtc(staleLock, Now.UtcDateTime);
        // The lock probe must not turn the old payload into a fresh cache.
        Assert.Equal(Now.UtcDateTime, File.GetLastWriteTimeUtc(staleLock));
        using var held = File.Open(Path.Combine(locked, RelaunchBuildCacheIdentity.LockFileName),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var bytes = new FileInfo(Payload(stale)).Length;
        var decisions = new List<EvidenceRetentionDecision>();

        RelaunchBuildCacheRetention.Sweep(CacheParent, RepositoryRoot, Now, decisions);

        Assert.False(Directory.Exists(stale));
        Assert.Equal("locked payload", File.ReadAllText(Payload(locked)));
        Assert.Equal("current payload", File.ReadAllText(Payload(current)));
        Assert.False(File.Exists(Path.Combine(current, RelaunchBuildCacheIdentity.LockFileName)));
        var deleted = Assert.Single(decisions, row => row.Action == EvidenceRetentionAction.Deleted);
        Assert.Equal(stale, deleted.Path);
        Assert.Equal(EvidenceArtifactFamily.RelaunchBuildCaches, deleted.Family);
        Assert.Equal(bytes, deleted.BytesReclaimed);
        Assert.Equal(bytes, decisions.Sum(row => row.BytesReclaimed));
        Assert.Equal("relaunch-build-cache-locked", Assert.Single(decisions, row => row.Path == locked).Reason);
        Assert.Equal("relaunch-build-cache-current-repository", Assert.Single(decisions, row => row.Path == current).Reason);
    }

    [Xunit.Fact]
    public void ProtectedCache_LockedAndAncient_IsSkippedBeforeLockProbe()
    {
        var current = Cache(RelaunchBuildCacheIdentity.RepositoryKey(RepositoryRoot), Now.AddDays(-100), "keep");
        using var held = File.Open(Path.Combine(current, RelaunchBuildCacheIdentity.LockFileName),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var decisions = new List<EvidenceRetentionDecision>();

        RelaunchBuildCacheRetention.Sweep(CacheParent, RepositoryRoot, Now, decisions);

        Assert.Equal("keep", File.ReadAllText(Payload(current)));
        var decision = Assert.Single(decisions);
        Assert.Equal(EvidenceRetentionAction.Preserved, decision.Action);
        Assert.Equal("relaunch-build-cache-current-repository", decision.Reason);
        Assert.Equal(0, decision.BytesReclaimed);
    }

    [Xunit.Theory]
    [Xunit.InlineData(47)]
    [Xunit.InlineData(48)]
    public void FreshPayloadOrExactBoundary_KeptWithoutCreatingLock(int hours)
    {
        var cache = Cache("3333333333333333", Now.AddHours(-hours), "fresh");
        Directory.SetLastWriteTimeUtc(cache, Now.AddDays(-20).UtcDateTime);
        var decisions = new List<EvidenceRetentionDecision>();

        RelaunchBuildCacheRetention.Sweep(CacheParent, RepositoryRoot, Now, decisions);

        Assert.Equal("fresh", File.ReadAllText(Payload(cache)));
        Assert.False(File.Exists(Path.Combine(cache, RelaunchBuildCacheIdentity.LockFileName)));
        Assert.Empty(decisions);
    }

    [Xunit.Fact]
    public void EmptyCacheAndUnrecognizedDirectory_AreKept()
    {
        var empty = Path.Combine(CacheParent, "4444444444444444");
        Directory.CreateDirectory(empty);
        File.WriteAllText(Path.Combine(empty, RelaunchBuildCacheIdentity.LockFileName), "");
        var unknown = Cache("operator-cache", Now.AddDays(-30), "keep unknown");
        var decisions = new List<EvidenceRetentionDecision>();

        RelaunchBuildCacheRetention.Sweep(CacheParent, RepositoryRoot, Now, decisions);

        Assert.True(Directory.Exists(empty));
        Assert.Equal("keep unknown", File.ReadAllText(Payload(unknown)));
        Assert.DoesNotContain(decisions, row => row.Action == EvidenceRetentionAction.Deleted);
    }

    [Xunit.Fact]
    public void LockedPayload_DeletionFailsWithoutCounting_ThenNextSweepReclaims()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires Windows delete-sharing semantics.");
        var cache = Cache("5555555555555555", Now.AddDays(-3), "locked payload");
        var bytes = new FileInfo(Payload(cache)).Length;
        var decisions = new List<EvidenceRetentionDecision>();
        using (File.Open(Payload(cache), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            RelaunchBuildCacheRetention.Sweep(CacheParent, RepositoryRoot, Now, decisions);
            Assert.True(Directory.Exists(cache));
            Assert.Equal(0, Assert.Single(decisions).BytesReclaimed);
            Assert.NotEqual(EvidenceRetentionAction.Deleted, decisions[0].Action);
        }
        decisions.Clear();

        RelaunchBuildCacheRetention.Sweep(CacheParent, RepositoryRoot, Now, decisions);

        Assert.False(Directory.Exists(cache));
        Assert.Equal(bytes, Assert.Single(decisions).BytesReclaimed);
    }

    [Xunit.Fact]
    public void RepositoryKey_MatchesProducerAcrossEquivalentRootSpellings()
    {
        var project = Path.Combine(RepositoryRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
        var producer = ConductorSelfRelaunch.CreateAppBuildCommand(project, Path.Combine(root, "output"));
        var key = Path.GetFileName(producer.IsolatedArtifactsRoot);

        Assert.Equal(key, RelaunchBuildCacheIdentity.RepositoryKey(RepositoryRoot));
        Assert.Equal(key, RelaunchBuildCacheIdentity.RepositoryKey(RepositoryRoot + Path.DirectorySeparatorChar));
        Assert.Equal(key, RelaunchBuildCacheIdentity.RepositoryKey(Path.Combine(RepositoryRoot, "child", "..")));
        if (OperatingSystem.IsWindows())
            Assert.Equal(key, RelaunchBuildCacheIdentity.RepositoryKey(RepositoryRoot.ToUpperInvariant()));
    }

    private string Cache(string key, DateTimeOffset timestamp, string contents)
    {
        var cache = Path.Combine(CacheParent, key);
        Directory.CreateDirectory(Path.GetDirectoryName(Payload(cache))!);
        File.WriteAllText(Payload(cache), contents);
        File.SetLastWriteTimeUtc(Payload(cache), timestamp.UtcDateTime);
        return cache;
    }

    private static string Payload(string cache) => Path.Combine(cache, "output", "nested", "payload.bin");

    public void Dispose()
    {
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
