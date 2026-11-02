using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: repositories, stores, hook records and clocks are private to each case.
public sealed class CleanupDebtEscalationDurabilityTests : GoalWorktreeTestBase
{
    [Xunit.Theory]
    [Xunit.InlineData("skip")]
    [Xunit.InlineData("failure")]
    [Xunit.InlineData("listing")]
    public void AttentionRaise_FailedOnce_NextTriggerCreatesItem(string trigger)
    {
        using var fixture = new DebtFixture();
        var blockedDirectory = Path.Combine(fixture.Repository, "attention-blocker");
        File.WriteAllText(blockedDirectory, "regular file blocks attention store creation");
        fixture.AttentionDirectory = blockedDirectory;

        Assert.Equal([fixture.Path], fixture.Sweep().LeftoverPaths);
        Assert.Single(fixture.Warnings, warning => warning.Operation == "cleanup-debt-attention-raise");
        Assert.True(HasAnyCleanupNeededRecord(fixture.Repository, fixture.Path));
        Assert.Equal(1, CleanupJournalSkipCount(fixture.Repository, fixture.Path));
        Assert.False(File.Exists(Path.Combine(fixture.WorkingAttentionDirectory, "collaboration-items.db")));

        fixture.AttentionDirectory = fixture.WorkingAttentionDirectory;
        if (trigger == "listing")
        {
            Assert.NotNull(Assert.Single(fixture.ListDebt()).EscalatedAtUtc);
        }
        else
        {
            if (trigger == "failure") fixture.Now = fixture.Now.AddDays(1).AddMinutes(1);
            Assert.Equal([fixture.Path], fixture.Sweep().LeftoverPaths);
            Assert.Equal(trigger == "failure" ? 2 : 1, fixture.DeletionAttempts);
        }

        var item = Assert.Single(fixture.Store.GetAttentionQueueAsync().GetAwaiter().GetResult());
        Assert.Equal(fixture.CorrelationKey, item.CorrelationKey);
        Assert.Equal(CollaborationItemType.Decision, item.Type);
        Assert.Equal(CollaborationItemStatus.Raised, item.Status);
        Assert.Equal(OrphanSweepTestFixture.ReferenceTime, Assert.Single(fixture.ListDebt()).EscalatedAtUtc);
    }

    [Xunit.Fact]
    public void Listing_AttentionStoreUnavailable_ReturnsEscalatedDebtAndLogsFailure()
    {
        using var fixture = new DebtFixture();
        var blockedDirectory = Path.Combine(fixture.Repository, "attention-blocker");
        File.WriteAllText(blockedDirectory, "regular file blocks attention store creation");
        fixture.AttentionDirectory = blockedDirectory;
        fixture.Sweep();
        Assert.Single(fixture.Warnings, warning => warning.Operation == "cleanup-debt-attention-raise");

        var debt = Assert.Single(fixture.ListDebt());

        Assert.Equal(fixture.Path, debt.Path);
        Assert.Equal(fixture.Now, debt.EscalatedAtUtc);
        Assert.Equal(2, fixture.Warnings.Count(warning => warning.Operation == "cleanup-debt-attention-raise"));
    }

    [Xunit.Fact]
    public void Listing_ClearedDelay_PreservesEscalatedJournalAndEnsuresAttention()
    {
        using var fixture = new DebtFixture();
        fixture.Sweep();
        var original = Assert.Single(fixture.ListDebt());
        Assert.True(HasAnyCleanupNeededRecord(fixture.Repository, fixture.Path));
        Assert.NotNull(original.EscalatedAtUtc);
        fixture.Now = fixture.Now.AddDays(1).AddMinutes(1);
        fixture.AttentionDirectory = Path.Combine(fixture.Repository, "recovered-attention");
        IReadOnlyList<GoalWorktreeCleanupDebt>? duringRetry = null;
        fixture.Delete = _ =>
        {
            // IsCleanupBackedOff has called ClearOrphanCleanupDelay, but no new
            // failure has recreated the backoff row at this delete boundary.
            Assert.False(HasAnyCleanupNeededRecord(fixture.Repository, fixture.Path));
            duringRetry = fixture.ListDebt();
            Assert.Equal(fixture.CorrelationKey,
                Assert.Single(fixture.Store.GetAttentionQueueAsync().GetAwaiter().GetResult()).CorrelationKey);
            return DebtFixture.FailedDelete();
        };

        fixture.Sweep();

        Assert.NotNull(duringRetry);
        var debt = Assert.Single(duringRetry);
        Assert.Equal(original.Path, debt.Path);
        Assert.Equal(original.Reason, debt.Reason);
        Assert.Equal(original.LastOperation, debt.LastOperation);
        Assert.Equal(original.FirstSeenUtc, debt.FirstSeenUtc);
        Assert.Equal(original.LastSeenUtc, debt.LastSeenUtc);
        Assert.Equal(original.ConsecutiveFailureCount, debt.ConsecutiveFailureCount);
        Assert.Equal(original.EscalatedAtUtc, debt.EscalatedAtUtc);
        Assert.Equal(TimeSpan.Zero, debt.RemainingWait);
        Assert.Equal(original.LastSeenUtc, debt.SkipUntilUtc);
        Assert.True(debt.SkipUntilUtc <= fixture.Now);
    }

    [Xunit.Fact]
    public void Observations_OpenAttentionItem_PreserveIdentityWithoutRepeatedEscalation()
    {
        using var fixture = new DebtFixture();
        fixture.Sweep();
        var original = Assert.Single(fixture.Store.GetAttentionQueueAsync().GetAwaiter().GetResult());

        for (var index = 0; index < 3; index++) fixture.Sweep();
        Assert.Equal(1, fixture.DeletionAttempts);
        fixture.Now = fixture.Now.AddDays(1).AddMinutes(1);
        fixture.Sweep();
        fixture.ListDebt();
        fixture.ListDebt();

        var item = Assert.Single(fixture.Store.ListAsync().GetAwaiter().GetResult());
        Assert.Equal(original.Id, item.Id);
        Assert.Equal(original.Body, item.Body);
        Assert.Equal(fixture.CorrelationKey, item.CorrelationKey);
        Assert.Equal(CollaborationItemStatus.Raised, item.Status);
        Assert.Single(fixture.Warnings, warning => warning.Operation == "cleanup-debt-escalated");
        Assert.Equal(1, fixture.LockHolderProbes);
        Assert.Equal(2, fixture.DeletionAttempts);
    }

    [Xunit.Fact]
    public void Cleanup_SuccessfulRetry_ResolvesAttentionAndRemovesDebt()
    {
        using var fixture = new DebtFixture();
        fixture.Sweep();
        var original = Assert.Single(fixture.Store.GetAttentionQueueAsync().GetAwaiter().GetResult());
        fixture.Now = fixture.Now.AddDays(1).AddMinutes(1);
        fixture.Delete = path =>
        {
            Directory.Delete(path, recursive: true);
            return GoalWorktreeDeleteResult.Success;
        };

        var result = fixture.Sweep();

        Assert.Equal(1, result.RemovedCount);
        Assert.False(Directory.Exists(fixture.Path));
        Assert.Empty(fixture.ListDebt());
        Assert.False(HasAnyCleanupNeededRecord(fixture.Repository, fixture.Path));
        Assert.Empty(fixture.Store.GetAttentionQueueAsync().GetAwaiter().GetResult());
        var resolved = Assert.Single(fixture.Store.ListAsync().GetAwaiter().GetResult());
        Assert.Equal(original.Id, resolved.Id);
        Assert.Equal(CollaborationItemStatus.Resolved, resolved.Status);
    }

    [Xunit.Fact]
    public void Observations_BelowThreshold_KeepBackoffWithoutCreatingAttentionStore()
    {
        using var fixture = new DebtFixture(threshold: 3);
        fixture.Sweep();
        fixture.Sweep();

        var debt = Assert.Single(fixture.ListDebt());

        Assert.Null(debt.EscalatedAtUtc);
        Assert.Equal(fixture.Now.AddMinutes(10), debt.SkipUntilUtc);
        Assert.Equal(TimeSpan.FromMinutes(10), debt.RemainingWait);
        Assert.Equal(1, debt.ConsecutiveFailureCount);
        Assert.Equal(1, fixture.DeletionAttempts);
        Assert.Equal(0, fixture.LockHolderProbes);
        Assert.DoesNotContain(fixture.Warnings, warning => warning.Operation.StartsWith("cleanup-debt-", StringComparison.Ordinal));
        Assert.False(File.Exists(Path.Combine(fixture.AttentionDirectory, "collaboration-items.db")));

        fixture.Now = fixture.Now.AddMinutes(11);
        fixture.Delete = _ =>
        {
            Assert.False(HasAnyCleanupNeededRecord(fixture.Repository, fixture.Path));
            Assert.Empty(fixture.ListDebt()); // A journal-only row below threshold stays hidden.
            return DebtFixture.FailedDelete();
        };
        fixture.Sweep();
        Assert.Equal(2, fixture.DeletionAttempts);
        Assert.Null(Assert.Single(fixture.ListDebt()).EscalatedAtUtc);
        Assert.False(File.Exists(Path.Combine(fixture.AttentionDirectory, "collaboration-items.db")));
    }

    private sealed class DebtFixture : IDisposable
    {
        private readonly OrphanSweepTestFixture orphan;
        private readonly int threshold;

        internal DebtFixture(int threshold = 1)
        {
            this.threshold = threshold;
            Repository = CreateSeededRepository();
            orphan = new OrphanSweepTestFixture(Repository);
            Path = orphan.AddDirectory("escalation-durability");
            WorkingAttentionDirectory = System.IO.Path.Combine(Repository, "attention");
            AttentionDirectory = WorkingAttentionDirectory;
        }

        internal string Repository { get; }
        internal string Path { get; }
        internal string WorkingAttentionDirectory { get; }
        internal string AttentionDirectory { get; set; }
        internal string CorrelationKey => $"worktree-cleanup-debt:{NormalizePath(Path)}";
        internal DateTimeOffset Now { get; set; } = OrphanSweepTestFixture.ReferenceTime;
        internal Func<string, GoalWorktreeDeleteResult> Delete { get; set; } = _ => FailedDelete();
        internal int DeletionAttempts { get; private set; }
        internal int LockHolderProbes { get; private set; }
        internal List<GoalWorktreeCleanupWarning> Warnings => orphan.Warnings;
        internal CollaborationItemStore Store => CollaborationItemStore.ForDirectory(AttentionDirectory);

        private GoalWorktreeCleanupHooks Hooks => orphan.Hooks with
        {
            CleanupUtcNow = () => Now,
            CleanupBackoffDuration = () => TimeSpan.FromMinutes(10),
            CleanupOptions = () => new(TimeSpan.FromMinutes(5), threshold, TimeSpan.FromDays(1)),
            CleanupAttentionStoreDirectory = () => AttentionDirectory,
            FindLockHoldersForCleanup = _ => { LockHolderProbes++; return []; },
            DeleteDirectoryForCleanup = path => { DeletionAttempts++; return Delete(path); }
        };

        internal GoalWorktreeSweepResult Sweep() => orphan.Sweep(hooks: Hooks);
        internal IReadOnlyList<GoalWorktreeCleanupDebt> ListDebt() => GoalWorktrees.ListCleanupDebt(Repository, Hooks);
        internal static GoalWorktreeDeleteResult FailedDelete() =>
            GoalWorktreeDeleteResult.Failed(GoalWorktreeDeleteFailureKind.Transient, "Injected cleanup failure.");
        public void Dispose() => DeleteDirectory(Repository);
    }
}
