using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private temporary folders, injected process inspection and no native labels.
public sealed class WorktreeFixtureScratchCleanupPreparationTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2030-01-01T12:00:00Z");
    private static readonly string[] Roots =
        ["mcg-wt", "mcg-git-helper-diag", "mcg-long-wt", "invoke-repo-script-tests"];

    [Fact]
    public void Prepare_AbandonedFixtures_RemovesOnlyEligibleRootsBeforeLabeling()
    {
        using var tree = new Tree();
        var owned = TempRootJanitor.BuildOwnedRootPath(tree.Scratch(Roots[0]), Environment.ProcessId);
        tree.Write(Path.Combine(owned, "owned.txt"));
        var dead = TempRootJanitor.BuildOwnedRootPath(tree.Scratch(Roots[0]), 123);
        tree.Write(Path.Combine(dead, "dead.txt"));
        foreach (var name in Roots.Skip(1)) tree.Write(tree.Scratch(name, "fixture", "old.txt"));
        var other = tree.Write(tree.Scratch("unrelated", "keep.txt"));
        var outside = tree.Write(Path.Combine(tree.Root, "source.txt"));
        var labels = new Labels(() =>
        {
            Assert.True(Directory.Exists(owned));
            Assert.False(Directory.Exists(dead));
            foreach (var name in Roots.Skip(1)) Assert.False(Directory.Exists(tree.Scratch(name)));
        });
        var inspected = new List<int>();
        var cleaner = new WorktreeFixtureScratchCleaner(utcNow: () => Now, inspect: pid =>
        {
            inspected.Add(pid);
            return pid == Environment.ProcessId ? ProcessInspectionStatus.Available : ProcessInspectionStatus.Exited;
        });

        var result = new WorkerSandboxPreparer(labels).Prepare(tree.Root, tree.Sandbox, cleaner);

        Assert.False(result.RequiresRecovery);
        Assert.True(result.WorktreeRecursiveRelabel);
        Assert.Equal(1, labels.RecursiveCalls);
        Assert.Contains(Environment.ProcessId, inspected);
        Assert.Contains(123, inspected);
        foreach (var path in new[] { other, outside, Path.Combine(owned, "owned.txt") })
            Assert.Equal("sentinel", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(ProcessInspectionStatus.AccessDenied)]
    [InlineData(ProcessInspectionStatus.NativeFailure)]
    public void Clean_UncertainOwnerAndRecentUnowned_PreservesBoth(ProcessInspectionStatus status)
    {
        using var tree = new Tree();
        var owned = TempRootJanitor.BuildOwnedRootPath(tree.Scratch(Roots[0]), 123);
        tree.Write(Path.Combine(owned, "file"));
        var recent = tree.Write(tree.Scratch(Roots[1], "fixture", "recent"));
        File.SetLastWriteTimeUtc(recent, Now.UtcDateTime);
        new WorktreeFixtureScratchCleaner(utcNow: () => Now, inspect: _ => status).Clean(tree.Root);
        Assert.True(Directory.Exists(owned));
        Assert.True(File.Exists(recent));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Prepare_CleanupBudgetExpires_ReturnsLabelerResult(bool succeeds)
    {
        using var tree = new Tree();
        tree.Write(tree.Scratch(Roots[1], "fixture", "file"));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var expired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var deletes = 0;
        var cleaner = new WorktreeFixtureScratchCleaner(utcNow: () => Now,
            waitForBudget: (_, _) => expired.Task,
            deleteEntry: (_, _) =>
            {
                Interlocked.Increment(ref deletes);
                entered.SetResult();
                try
                {
                    Assert.True(release.Wait(TimeSpan.FromSeconds(30)), "Deletion release was not signaled.");
                }
                finally { finished.SetResult(); }
            });
        var labels = new Labels(() => { }) { Succeeds = succeeds };
        WorktreeFixtureScratchCleanupResult? cleanup = null;
        var preparation = Task.Run(() => new WorkerSandboxPreparer(labels)
            .Prepare(tree.Root, tree.Sandbox, cleaner, result => cleanup = result));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            expired.SetResult();
            var result = await preparation.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.True(cleanup!.TimedOut);
            Assert.Equal(1, labels.RecursiveCalls);
            Assert.Equal(!succeeds, result.RequiresRecovery);
            Assert.Equal(succeeds, result.WorktreeRecursiveRelabel);
            Assert.False(release.IsSet);
        }
        finally
        {
            release.Set();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await preparation.WaitAsync(TimeSpan.FromSeconds(30));
            await cleaner.Completion!.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(1, deletes);
    }

    [Fact]
    public void Clean_HomeRepository_PreservesFixtures()
    {
        using var tree = new Tree();
        File.Delete(Path.Combine(tree.Root, ".git"));
        Directory.CreateDirectory(Path.Combine(tree.Root, ".git"));
        var file = tree.Write(tree.Scratch(Roots[1], "fixture", "file"));
        var result = new WorktreeFixtureScratchCleaner(utcNow: () => Now).Clean(tree.Root);
        Assert.False(result.FoundRoots);
        Assert.Equal("sentinel", File.ReadAllText(file));
    }

    [Fact]
    public void Prepare_SecondAttempt_CleansAgain()
    {
        using var tree = new Tree();
        var preparer = new WorkerSandboxPreparer(new Labels(() => { }) { Succeeds = false });
        var cleaner = new WorktreeFixtureScratchCleaner(utcNow: () => Now);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            tree.Write(tree.Scratch(Roots[1], "fixture", "file"));
            Assert.True(preparer.Prepare(tree.Root, tree.Sandbox, cleaner).RequiresRecovery);
            Assert.False(Directory.Exists(tree.Scratch(Roots[1])));
        }
    }

    [Fact]
    public void Prepare_DeletionFails_PreservesLabelerFailure()
    {
        using var tree = new Tree();
        tree.Write(tree.Scratch(Roots[1], "fixture", "file"));
        var attempted = 0;
        var cleaner = new WorktreeFixtureScratchCleaner(utcNow: () => Now, deleteEntry: (_, _) =>
        {
            attempted++;
            throw new IOException("Injected deletion failure");
        });
        var labels = new Labels(() => { }) { Succeeds = false };
        var result = new WorkerSandboxPreparer(labels).Prepare(tree.Root, tree.Sandbox, cleaner);
        Assert.True(attempted > 0);
        Assert.True(result.RequiresRecovery);
        Assert.Equal(1, labels.RecursiveCalls);
        Assert.Contains("Failed to apply inheritable Low integrity label", result.RecoveryAction!.Reason);
        Assert.DoesNotContain("Injected deletion failure", result.RecoveryAction.Reason);
    }

    private sealed class Labels(Action beforeRecursive) : IWorkerIntegrityLabeler
    {
        internal bool Succeeds = true;
        internal int RecursiveCalls;
        private bool labeled;
        public IntegrityLabelState Query(string path) => new(true, labeled, labeled, Medium: !labeled);
        public bool SetIntegrity(string path, string level, bool recursive)
        {
            if (recursive) { RecursiveCalls++; beforeRecursive(); }
            if (Succeeds) labeled = true;
            return Succeeds;
        }
    }

    private sealed class Tree : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "fixture-cleanup", Guid.NewGuid().ToString("N"));
        internal string Sandbox => Path.Combine(Root, ".mcg-sandbox");
        internal Tree()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, ".git"), "gitdir: unused");
        }
        internal string Scratch(params string[] parts) => Path.Combine([Root, ".scratch", .. parts]);
        internal string Write(string path)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "sentinel");
            File.SetLastWriteTimeUtc(path, DateTime.Parse("2000-01-01T00:00:00Z").ToUniversalTime());
            for (var parent = Path.GetDirectoryName(path); parent != Root && parent is not null;
                 parent = Path.GetDirectoryName(parent))
                Directory.SetLastWriteTimeUtc(parent, DateTime.Parse("2000-01-01T00:00:00Z").ToUniversalTime());
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
