using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each instance owns a temporary tree, a fixed clock and a private registry stub.
public sealed class UnstoredGoalBuildRootLeaseAbsentTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("lease-absent-retention-").FullName;
    private static readonly DateTimeOffset Now = new(2030, 1, 31, 0, 0, 0, TimeSpan.Zero);
    private DotnetBuildStorageRoot Storage => new(Path.Combine(_temp, "isolated"));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AbsentLease_AllFilesOlderThanWindow_ReclaimsRootAndCountsBytes(bool lockOnly)
    {
        var root = CreateRoot();
        WriteLease(root, Now.AddDays(-30));
        File.Delete(LeasePath(root));
        if (lockOnly) File.WriteAllBytes(Path.Combine(root, "lease", "lease.lock"), new byte[17]);
        AgeTree(root);
        Assert.True(Directory.Exists(Path.Combine(root, "lease")));
        Assert.False(File.Exists(LeasePath(root)));
        if (!lockOnly) Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(root, "lease")));

        var result = Run();

        Assert.Equal(1, result.InspectedCount);
        Assert.Equal(1, result.ReclaimedCount);
        Assert.Equal(lockOnly ? 2955L : 2938L, result.BytesReclaimed);
        Assert.Equal(root, Assert.Single(result.ReclaimedRoots));
        Assert.False(Directory.Exists(root));
        Assert.Empty(result.Kept);
        Assert.EndsWith("kept=0 reasons=none", result.SummaryLine);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(Storage.RootPath, "goal-root-reclaim")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AbsentLease_FileNewerThanWindow_PreservesTreeAndReportsReason(bool leaseFolder)
    {
        var root = CreateRoot();
        if (!leaseFolder) Directory.Delete(Path.Combine(root, "lease"));
        AgeTree(root);
        File.SetLastWriteTimeUtc(Path.Combine(root, "payload.bin"), Now.AddDays(-1).UtcDateTime);
        var before = Snapshot(root);

        var result = Run();

        AssertKept(root, before, result, leaseFolder
            ? UnstoredGoalBuildRootKeepReason.LeaseAbsent : UnstoredGoalBuildRootKeepReason.WithinWindow);
        Assert.EndsWith(leaseFolder ? "reasons=lease-absent:1" : "reasons=within-window:1", result.SummaryLine);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("directory")]
    public void UnusableLeaseFile_AllFilesOld_KeptAsLeaseUnreadable(string contents)
    {
        var root = CreateRoot();
        if (contents == "directory") Directory.CreateDirectory(LeasePath(root));
        else File.WriteAllText(LeasePath(root), contents);
        AgeTree(root);
        var before = Snapshot(root);

        var result = Run();

        AssertKept(root, before, result, UnstoredGoalBuildRootKeepReason.LeaseUnreadable);
        Assert.EndsWith("reasons=lease-unreadable:1", result.SummaryLine);
    }

    [Fact]
    public void UnreadableExistingLease_AllFilesOld_KeptAsLeaseUnreadable()
    {
        var root = CreateRoot();
        WriteLease(root, Now.AddDays(-30));
        AgeTree(root);
        var before = Snapshot(root);
        UnstoredGoalBuildRootRetentionResult result;

        using (new FileStream(LeasePath(root), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            result = Run();

        AssertKept(root, before, result, UnstoredGoalBuildRootKeepReason.LeaseUnreadable);
    }

    [Fact]
    public void ValidLeaseInsideWindow_AllFilesOld_KeptAsWithinWindow()
    {
        var root = CreateRoot();
        WriteLease(root, Now.AddDays(-1));
        AgeTree(root);
        var before = Snapshot(root);

        var result = Run();

        AssertKept(root, before, result, UnstoredGoalBuildRootKeepReason.WithinWindow);
        Assert.EndsWith("reasons=within-window:1", result.SummaryLine);
    }

    private string CreateRoot()
    {
        var root = Path.Combine(Storage.RootPath, "goals", "aaaa0001");
        Directory.CreateDirectory(Path.Combine(root, "lease"));
        File.WriteAllBytes(Path.Combine(root, "payload.bin"), new byte[2938]);
        return root;
    }

    private void WriteLease(string root, DateTimeOffset lastUsed) =>
        File.WriteAllText(LeasePath(root), JsonSerializer.Serialize(new
        {
            goalId = "aaaa0001000000000000000000000000",
            ownerProcessId = 12345,
            lastUsedAt = lastUsed,
            repositoryRoot = Path.Combine(_temp, "repository")
        }));

    private static string LeasePath(string root) => Path.Combine(root, "lease", "lease.json");

    private static void AgeTree(string root)
    {
        var old = Now.AddDays(-30).UtcDateTime;
        foreach (var path in Directory.GetFiles(root, "*", SearchOption.AllDirectories))
            File.SetLastWriteTimeUtc(path, old);
        foreach (var path in Directory.GetDirectories(root, "*", SearchOption.AllDirectories))
            Directory.SetLastWriteTimeUtc(path, old);
        Directory.SetLastWriteTimeUtc(root, old);
    }

    private UnstoredGoalBuildRootRetentionResult Run() =>
        new UnstoredGoalBuildRootRetention(Storage, new EmptyOwnedRoots(), _ => false, new FixedTimeProvider())
            .Run([], [], 7, 25, 1_073_741_824, null);

    private static void AssertKept(string root, string[] before, UnstoredGoalBuildRootRetentionResult result,
        UnstoredGoalBuildRootKeepReason reason)
    {
        Assert.True(Directory.Exists(root));
        Assert.Equal(before, Snapshot(root));
        Assert.Equal(1, result.InspectedCount);
        Assert.Equal(0, result.ReclaimedCount);
        Assert.Equal(0, result.BytesReclaimed);
        Assert.Empty(result.ReclaimedRoots);
        var kept = Assert.Single(result.Kept);
        Assert.Equal(reason, kept.Key);
        Assert.Equal(1, kept.Value);
    }

    private static string[] Snapshot(string root) =>
    [
        .. Directory.GetDirectories(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => "directory:" + Path.GetRelativePath(root, path)),
        .. Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => "file:" + Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
    ];

    private sealed class FixedTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class EmptyOwnedRoots : IOwnedRunRootStore
    {
        public IReadOnlySet<string> ReadRegisteredPaths(IReadOnlyList<string> canonicalPaths) => new HashSet<string>();
        public IReadOnlyList<OwnedRunRootEntry> ReadBatch(long afterId, int maxCount, DateTimeOffset now) =>
            throw new NotSupportedException();
        public void Register(string canonicalPath, OwnedRunRootPurpose purpose, SpawnProcessIdentity owner,
            string? goalId, string? dispatchId, DateTimeOffset now) => throw new NotSupportedException();
        public void Release(string canonicalPath, OwnedRunRootReleaseOutcome outcome, DateTimeOffset now) =>
            throw new NotSupportedException();
        public void MarkRemoved(string canonicalPath, DateTimeOffset now) => throw new NotSupportedException();
        public void MarkCleanupFailure(string canonicalPath, string holderEvidence, DateTimeOffset nextAttemptAfter,
            DateTimeOffset now) => throw new NotSupportedException();
        public void RecordRetention(string canonicalPath, string evidence, DateTimeOffset now) => throw new NotSupportedException();
    }

    public void Dispose() => Directory.Delete(_temp, recursive: true);
}
