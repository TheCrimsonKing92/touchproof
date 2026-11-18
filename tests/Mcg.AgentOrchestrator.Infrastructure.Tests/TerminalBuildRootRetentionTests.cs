using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: every filesystem root and SQLite registry belongs to this test instance.
public sealed class TerminalBuildRootRetentionTests : IDisposable
{
    private readonly string _temp = Directory.CreateTempSubdirectory("terminal-retention-").FullName;
    private readonly DateTimeOffset _now = DateTimeOffset.UtcNow;
    private DotnetBuildStorageRoot Storage => new(Path.Combine(_temp, "isolated"));
    private string ReclaimFolder => Path.Combine(Storage.RootPath, "goal-root-reclaim");

    [Fact]
    public void OldUnreferencedTerminalRootsAreReclaimedWithinBudget()
    {
        var roots = CreateThree();
        var retention = Retention();
        var first = retention.Run(roots.Select(pair => pair.Fact).ToArray(), 3, 2);
        Assert.Equal(2, first.ReclaimedCount);
        Assert.True(first.BytesReclaimed >= 2000);
        Assert.Equal(1, first.Skipped[TerminalBuildRootSkipReason.BudgetExhausted]);
        Assert.StartsWith("SWEEP_BUILD_ROOT_RETENTION window-days=3 reclaimed=2", first.SummaryLine);
        Assert.Equal(roots.Take(2).Select(pair => pair.Root), first.ReclaimedRoots);
        Assert.False(Directory.Exists(roots[0].Root));
        Assert.False(Directory.Exists(roots[1].Root));
        Assert.True(Directory.Exists(roots[2].Root));
        Assert.Empty(Directory.GetDirectories(ReclaimFolder));
        var second = retention.Run(roots.Select(pair => pair.Fact).ToArray(), 3, 2);
        Assert.Equal(1, second.ReclaimedCount);
        Assert.Equal(roots[2].Root, Assert.Single(second.ReclaimedRoots));
        Assert.False(Directory.Exists(roots[2].Root));
        Assert.Empty(Directory.GetDirectories(ReclaimFolder));
    }

    [Theory]
    [InlineData("Active", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("Parked", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("WaitingForHuman", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("Verifying", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("Verified", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("AcceptanceFailed", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("Failed", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("Draft", TerminalBuildRootSkipReason.GoalNotTerminal)]
    [InlineData("not-stored", TerminalBuildRootSkipReason.GoalNotStored)]
    [InlineData("ambiguous", TerminalBuildRootSkipReason.GoalPrefixAmbiguous)]
    [InlineData("missing-lease", TerminalBuildRootSkipReason.LeaseUnreadable)]
    [InlineData("wrong-id", TerminalBuildRootSkipReason.LeaseUnreadable)]
    [InlineData("registered", TerminalBuildRootSkipReason.RegisteredOwnedRoot)]
    [InlineData("recent", TerminalBuildRootSkipReason.WithinWindow)]
    [InlineData("live", TerminalBuildRootSkipReason.OwnerLive)]
    [InlineData("missing-updated", TerminalBuildRootSkipReason.WithinWindow)]
    [InlineData("recent-updated", TerminalBuildRootSkipReason.WithinWindow)]
    [InlineData("missing-last-used", TerminalBuildRootSkipReason.LeaseUnreadable)]
    public void NonTerminalAndReferencedRootsAreNeverTouched(string shape, object expectedReason)
    {
        // object keeps the public test signature independent of the internal production enum.
        var reason = (TerminalBuildRootSkipReason)expectedReason;
        var goal = new GoalId("11111111000000000000000000000000");
        var root = CreateRoot(goal);
        var facts = new List<StoredGoalRetentionFact>
        { new(goal.Value, Enum.TryParse<GoalStatus>(shape, out var status) ? status : GoalStatus.Completed, _now.AddDays(-10)) };
        var registry = Registry();
        if (shape == "not-stored") facts.Clear();
        if (shape == "ambiguous") facts.Add(new("11111111ffffffffffffffffffffffff", GoalStatus.Parked, _now.AddDays(-10)));
        if (shape == "missing-lease") File.Delete(Path.Combine(root, "lease", "lease.json"));
        if (shape == "wrong-id") EditLease(root, "goalId", JsonValue.Create("11111111ffffffffffffffffffffffff"));
        if (shape == "recent") EditLease(root, "lastUsedAt", JsonValue.Create(_now.AddHours(-12)));
        if (shape == "missing-last-used") EditLease(root, "lastUsedAt", null);
        if (shape == "missing-updated") facts[0] = facts[0] with { UpdatedAt = null };
        if (shape == "recent-updated") facts[0] = facts[0] with { UpdatedAt = _now.AddHours(-12) };
        if (shape == "registered")
            registry.Register(root, OwnedRunRootPurpose.RunAttempt,
                new SpawnProcessIdentity(999999, _now.AddDays(-10), Path.Combine(_temp, "dotnet.exe")),
                goal.Value, null, _now.AddDays(-10));
        var before = Snapshot(root);
        var result = new TerminalBuildRootRetention(Storage, registry, _ => shape == "live", TimeProvider.System)
            .Run(facts, 1, 10);
        Assert.Equal(before, Snapshot(root));
        Assert.Equal(0, result.ReclaimedCount);
        var skip = Assert.Single(result.Skipped);
        Assert.Equal(reason, skip.Key);
        Assert.Equal(1, skip.Value);
    }

    [Fact]
    public void HeldRootIsSkippedReportedAndReclaimedOnceReleased()
    {
        var root = CreateRoot(new GoalId("11111111000000000000000000000000"));
        var facts = new[] { new StoredGoalRetentionFact("11111111000000000000000000000000", GoalStatus.Completed, _now.AddDays(-10)) };
        var before = Snapshot(root);
        var retention = Retention();
        using (var held = new FileStream(Path.Combine(root, "payload.bin"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var first = retention.Run(facts, 1, 2);
            Assert.Equal(0, first.ReclaimedCount);
            Assert.True(Directory.Exists(root));
            Assert.Equal(1, first.Skipped[TerminalBuildRootSkipReason.Held]);
            Assert.Contains("held:1", first.SummaryLine);
        }
        Assert.Equal(before, Snapshot(root));
        Assert.Equal(1, retention.Run(facts, 1, 2).ReclaimedCount);
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void ZeroDaysTouchesNothing()
    {
        var roots = CreateThree();
        var snapshots = roots.Select(pair => Snapshot(pair.Root)).ToArray();
        var result = Retention().Run(roots.Select(pair => pair.Fact).ToArray(), 0, 2);
        Assert.Equal(0, result.ReclaimedCount);
        Assert.Equal(0, result.BytesReclaimed);
        Assert.Empty(result.Skipped);
        Assert.Empty(result.ReclaimedRoots);
        Assert.Empty(result.SummaryLine);
        Assert.False(Directory.Exists(ReclaimFolder));
        for (var index = 0; index < roots.Length; index++)
            Assert.Equal(snapshots[index], Snapshot(roots[index].Root));
    }

    [Fact]
    public void FailedRemovalIsRetriedBeforeNewRootsAndConsumesBudget()
    {
        var roots = CreateThree();
        var readOnly = Path.Combine(roots[0].Root, "payload.bin");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        string? renamedFile = null;
        try
        {
            var retention = Retention();
            var facts = roots.Select(pair => pair.Fact).ToArray();
            var failed = retention.Run(facts, 1, 1);
            Assert.Equal(0, failed.ReclaimedCount);
            Assert.Equal(0, failed.BytesReclaimed);
            Assert.Equal(1, failed.Skipped[TerminalBuildRootSkipReason.RemovalFailed]);
            Assert.Equal(2, failed.Skipped[TerminalBuildRootSkipReason.BudgetExhausted]);
            Assert.False(Directory.Exists(roots[0].Root));
            var renamed = Assert.Single(Directory.GetDirectories(ReclaimFolder));
            renamedFile = Path.Combine(renamed, "payload.bin");
            Assert.True(File.Exists(renamedFile));
            File.SetAttributes(renamedFile, FileAttributes.Normal);
            var retry = retention.Run(facts, 1, 1);
            Assert.Equal(1, retry.ReclaimedCount);
            Assert.True(retry.BytesReclaimed >= 1000);
            Assert.Equal(renamed, Assert.Single(retry.ReclaimedRoots));
            Assert.Equal(2, retry.Skipped[TerminalBuildRootSkipReason.BudgetExhausted]);
            Assert.All(roots.Skip(1), pair => Assert.True(Directory.Exists(pair.Root)));
            Assert.Empty(Directory.GetDirectories(ReclaimFolder));
        }
        finally
        {
            if (File.Exists(readOnly)) File.SetAttributes(readOnly, FileAttributes.Normal);
            if (renamedFile is not null && File.Exists(renamedFile)) File.SetAttributes(renamedFile, FileAttributes.Normal);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void SizeBudget_RecentRoots_ReclaimsOldestLeasesUntilSatisfied(int count)
    {
        var roots = CreateRecentThree();
        var ordered = new[] { roots[2].Root, roots[0].Root, roots[1].Root };
        var total = roots.Sum(pair => Size(pair.Root));
        var budget = total - ordered.Take(count).Sum(Size);

        var result = Retention().Run(roots.Select(pair => pair.Fact).ToArray(), 1, 10, budget);

        Assert.Equal(ordered.Take(count), result.ReclaimedRoots);
        Assert.Equal(count, result.ReclaimedCount);
        Assert.Equal(total - budget, result.BytesReclaimed);
        Assert.Equal(budget, result.RetainedBytes.GoalRootBytes);
        Assert.Equal(budget, result.RetainedBytes.WindowBytes);
        Assert.All(ordered.Take(count), root => Assert.False(Directory.Exists(root)));
        Assert.All(ordered.Skip(count), root => Assert.True(Directory.Exists(root)));
    }

    [Fact]
    public void SizeBudget_Null_LeavesRecentRootsUnchanged()
    {
        var roots = CreateRecentThree();
        var before = roots.Select(pair => Snapshot(pair.Root)).ToArray();
        var total = roots.Sum(pair => Size(pair.Root));

        var result = Retention().Run(roots.Select(pair => pair.Fact).ToArray(), 1, 10, null);

        Assert.Empty(result.ReclaimedRoots);
        Assert.Equal(total, result.RetainedBytes.GoalRootBytes);
        Assert.Equal(total, result.RetainedBytes.WindowBytes);
        for (var index = 0; index < roots.Length; index++)
            Assert.Equal(before[index], Snapshot(roots[index].Root));
    }

    [Fact]
    public void SizeBudget_ZeroWithAgeDisabled_ReclaimsOnlyUpToSweepCap()
    {
        var roots = CreateRecentThree();
        var remaining = Size(roots[1].Root);

        var result = Retention().Run(roots.Select(pair => pair.Fact).ToArray(), 0, 2, 0);

        Assert.Equal(new[] { roots[2].Root, roots[0].Root }, result.ReclaimedRoots);
        Assert.False(Directory.Exists(roots[2].Root));
        Assert.False(Directory.Exists(roots[0].Root));
        Assert.True(Directory.Exists(roots[1].Root));
        Assert.Equal(1, result.Skipped[TerminalBuildRootSkipReason.BudgetExhausted]);
        Assert.Equal(remaining, result.RetainedBytes.GoalRootBytes);
        Assert.Equal(0, result.RetainedBytes.WindowBytes);
    }

    [Fact]
    public void SizeBudget_Satisfied_DoesNotDisableAgeReclamation()
    {
        var roots = CreateThree();
        var result = Retention().Run(roots.Select(pair => pair.Fact).ToArray(), 1, 10,
            roots.Sum(pair => Size(pair.Root)));
        Assert.Equal(3, result.ReclaimedCount);
        Assert.All(roots, pair => Assert.False(Directory.Exists(pair.Root)));
        Assert.Equal(0, result.RetainedBytes.GoalRootBytes);
    }

    [Theory]
    [InlineData(GoalStatus.Active)]
    [InlineData(GoalStatus.Parked)]
    [InlineData(GoalStatus.WaitingForHuman)]
    [InlineData(GoalStatus.Verifying)]
    [InlineData(GoalStatus.Verified)]
    [InlineData(GoalStatus.AcceptanceFailed)]
    [InlineData(GoalStatus.Failed)]
    [InlineData(GoalStatus.Draft)]
    public void SizeBudget_Zero_NeverReclaimsNonTerminalGoals(GoalStatus status)
    {
        var goal = new GoalId("11111111000000000000000000000000");
        var root = CreateRoot(goal);
        var before = Snapshot(root);
        var result = Retention().Run([new(goal.Value, status, _now.AddDays(-10))], 1, 10, 0);
        Assert.Empty(result.ReclaimedRoots);
        Assert.Equal(before, Snapshot(root));
        Assert.Equal(Size(root), result.RetainedBytes.NotTerminalBytes);
        Assert.Equal(1, result.Skipped[TerminalBuildRootSkipReason.GoalNotTerminal]);
    }

    [Fact]
    public void SizeBudget_Exceeded_ProtectsAndSeparatelyCountsOwnedAndParkedRoots()
    {
        var roots = CreateRecentThree();
        var facts = roots.Select(pair => pair.Fact).ToList();
        facts[0] = facts[0] with { Status = GoalStatus.Parked };
        const int liveOwner = 424242;
        EditLease(roots[2].Root, "ownerProcessId", JsonValue.Create(liveOwner));
        var registry = Registry();
        registry.Register(roots[1].Root, OwnedRunRootPurpose.RunAttempt,
            new SpawnProcessIdentity(liveOwner, _now.AddDays(-10), Path.Combine(_temp, "dotnet.exe")),
            facts[1].GoalId, null, _now.AddDays(-10));
        EditLease(roots[1].Root, "ownerProcessId", JsonValue.Create(liveOwner));
        var eligibleGoal = new GoalId("44444444000000000000000000000000");
        var eligible = CreateRoot(eligibleGoal);
        EditLease(eligible, "lastUsedAt", JsonValue.Create(_now.AddHours(-1)));
        facts.Add(new(eligibleGoal.Value, GoalStatus.Completed, _now.AddDays(-10)));
        var snapshots = roots.Select(pair => Snapshot(pair.Root)).ToArray();
        var sizes = roots.Select(pair => Size(pair.Root)).ToArray();
        var reclaimedBytes = Size(eligible);

        var result = new TerminalBuildRootRetention(Storage, registry, pid => pid == liveOwner,
            new FixedTimeProvider(_now)).Run(facts, 1, 10, 0);

        Assert.Equal(eligible, Assert.Single(result.ReclaimedRoots));
        Assert.False(Directory.Exists(eligible));
        Assert.Equal(reclaimedBytes, result.BytesReclaimed);
        for (var index = 0; index < roots.Length; index++)
            Assert.Equal(snapshots[index], Snapshot(roots[index].Root));
        Assert.Equal(sizes.Sum(), result.RetainedBytes.GoalRootBytes);
        Assert.Equal(sizes[0], result.RetainedBytes.NotTerminalBytes);
        Assert.Equal(sizes[1], result.RetainedBytes.RegisteredOwnerBytes);
        Assert.Equal(sizes[2], result.RetainedBytes.LiveOwnerBytes);
        Assert.Equal(sizes[1] + sizes[2], result.RetainedBytes.OwnerBytes);
        Assert.Equal(0, result.RetainedBytes.WindowBytes);
        Assert.Equal(3, result.Skipped.Count);
        Assert.Equal(1, result.Skipped[TerminalBuildRootSkipReason.GoalNotTerminal]);
        Assert.Equal(1, result.Skipped[TerminalBuildRootSkipReason.RegisteredOwnedRoot]);
        Assert.Equal(1, result.Skipped[TerminalBuildRootSkipReason.OwnerLive]);
        Assert.Contains($"retained-owner-bytes={sizes[1] + sizes[2]}", result.SummaryLine);
    }

    [Fact]
    public void Receipt_MixedRetainedRoots_ReportsMeasuredBytesAndPreservesPrefix()
    {
        var roots = CreateThree();
        var facts = roots.Select(pair => pair.Fact).ToArray();
        facts[0] = facts[0] with { Status = GoalStatus.Parked };
        EditLease(roots[1].Root, "lastUsedAt", JsonValue.Create(_now.AddHours(-1)));
        const int liveOwner = 424242;
        EditLease(roots[2].Root, "ownerProcessId", JsonValue.Create(liveOwner));
        var snapshots = roots.Select(pair => Snapshot(pair.Root)).ToArray();
        var sizes = roots.Select(pair => Size(pair.Root)).ToArray();

        var result = new TerminalBuildRootRetention(Storage, Registry(), pid => pid == liveOwner,
            new FixedTimeProvider(_now)).Run(facts, 1, 10);

        Assert.Empty(result.ReclaimedRoots);
        for (var index = 0; index < roots.Length; index++)
            Assert.Equal(snapshots[index], Snapshot(roots[index].Root));
        Assert.Equal(sizes[0], result.RetainedBytes.NotTerminalBytes);
        Assert.Equal(sizes[1], result.RetainedBytes.WindowBytes);
        Assert.Equal(sizes[2], result.RetainedBytes.LiveOwnerBytes);
        Assert.Equal(0, result.RetainedBytes.RegisteredOwnerBytes);
        Assert.Equal(
            "SWEEP_BUILD_ROOT_RETENTION window-days=1 reclaimed=0 bytes=0 skipped=3 " +
            "reasons=goal-not-terminal:1,within-window:1,owner-live:1 " +
            $"size-budget-bytes=none goal-root-bytes={sizes.Sum()} " +
            $"retained-not-terminal-bytes={sizes[0]} retained-window-bytes={sizes[1]} " +
            $"retained-owner-bytes={sizes[2]} incomplete-roots=0", result.SummaryLine);
    }

    [Fact]
    public void SizeBudget_JunctionTarget_ExcludedFromPressureAndRetainedBytes()
    {
        var parkedGoal = new GoalId("11111111000000000000000000000000");
        var terminalGoal = new GoalId("22222222000000000000000000000000");
        var parked = CreateRoot(parkedGoal);
        var terminal = CreateRoot(terminalGoal);
        EditLease(terminal, "lastUsedAt", JsonValue.Create(_now.AddHours(-1)));
        var parkedBytes = Size(parked);
        var terminalBytes = Size(terminal);
        var terminalBefore = Snapshot(terminal);
        var target = Directory.CreateDirectory(Path.Combine(_temp, "external-target")).FullName;
        var payload = Path.Combine(target, "large.bin");
        File.WriteAllBytes(payload, new byte[5_000_000]);
        var targetBefore = Snapshot(target);
        var budget = parkedBytes + terminalBytes + 100;
        Assert.True(budget < parkedBytes + terminalBytes + new FileInfo(payload).Length);
        var junction = Path.Combine(parked, "outside");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var startInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe"), WorkingDirectory = _temp,
                    UseShellExecute = false, CreateNoWindow = true,
                    RedirectStandardOutput = true, RedirectStandardError = true,
                    ArgumentList = { "/d", "/c", "mklink", "/J", junction, target }
                };
                startInfo.Environment.Clear();
                startInfo.Environment["SystemRoot"] = Path.GetDirectoryName(Environment.SystemDirectory)!;
                var created = TestChildProcessCapture.Run(startInfo);
                Assert.True(created.ExitCode == 0, created.Stdout + created.Stderr);
            }
            else Directory.CreateSymbolicLink(junction, target);
            Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
            Assert.True(File.Exists(Path.Combine(junction, "large.bin")));

            var result = new TerminalBuildRootRetention(Storage, Registry(), _ => false,
                new FixedTimeProvider(_now)).Run(
                [new(parkedGoal.Value, GoalStatus.Parked, _now.AddDays(-10)),
                 new(terminalGoal.Value, GoalStatus.Completed, _now.AddDays(-10))], 1, 10, budget);

            Assert.Empty(result.ReclaimedRoots);
            Assert.Equal(0, result.BytesReclaimed);
            Assert.True(Directory.Exists(parked));
            Assert.True(Directory.Exists(junction));
            Assert.Equal(terminalBefore, Snapshot(terminal));
            Assert.Equal(targetBefore, Snapshot(target));
            Assert.Equal(parkedBytes + terminalBytes, result.RetainedBytes.GoalRootBytes);
            Assert.Equal(parkedBytes, result.RetainedBytes.NotTerminalBytes);
            Assert.Equal(terminalBytes, result.RetainedBytes.WindowBytes);
            Assert.Equal(0, result.RetainedBytes.OwnerBytes);
            Assert.Equal(0, result.RetainedBytes.IncompleteRoots);
            Assert.Equal(
                "SWEEP_BUILD_ROOT_RETENTION window-days=1 reclaimed=0 bytes=0 skipped=2 " +
                "reasons=goal-not-terminal:1,within-window:1 " +
                $"size-budget-bytes={budget} goal-root-bytes={parkedBytes + terminalBytes} " +
                $"retained-not-terminal-bytes={parkedBytes} retained-window-bytes={terminalBytes} " +
                "retained-owner-bytes=0 incomplete-roots=0", result.SummaryLine);
        }
        finally
        {
            if (Directory.Exists(junction)) Directory.Delete(junction);
        }
    }

    [Fact]
    public void SizeBudget_IncompleteOldestRoot_UsesLowerBoundButKeepsThatRoot()
    {
        var roots = CreateRecentThree();
        var incomplete = roots[2].Root;
        var lowerBound = 17L;
        var budget = Size(roots[1].Root) + lowerBound;
        var result = new TerminalBuildRootRetention(Storage, Registry(), _ => false, TimeProvider.System,
            root => root == incomplete ? new(lowerBound, false) : new(Size(root), true))
            .Run(roots.Select(pair => pair.Fact).ToArray(), 1, 10, budget);

        Assert.Equal(roots[0].Root, Assert.Single(result.ReclaimedRoots));
        Assert.True(Directory.Exists(incomplete));
        Assert.Equal(budget, result.RetainedBytes.GoalRootBytes);
        Assert.Equal(1, result.RetainedBytes.IncompleteRoots);
        Assert.Contains($"goal-root-bytes=>={budget}", result.SummaryLine);
        Assert.Contains($"retained-window-bytes=>={budget}", result.SummaryLine);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    public void IncompleteSize_UnknownBytes_StillAllowsAgeReclamation(int days, int expected)
    {
        var root = CreateRoot(new GoalId("11111111000000000000000000000000"));
        var fact = new StoredGoalRetentionFact("11111111000000000000000000000000", GoalStatus.Completed, _now.AddDays(-10));
        var result = new TerminalBuildRootRetention(Storage, Registry(), _ => false, TimeProvider.System,
            _ => new(null, false)).Run([fact], days, 10, 0);

        Assert.Equal(expected, result.ReclaimedCount);
        Assert.Equal(expected == 0, Directory.Exists(root));
        if (expected == 0)
        {
            Assert.Null(result.RetainedBytes.GoalRootBytes);
            Assert.Equal(1, result.RetainedBytes.IncompleteRoots);
            Assert.Contains("goal-root-bytes=unknown", result.SummaryLine);
        }
    }

    [Fact]
    public void SizeBudget_Negative_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Retention().Run([], 0, 10, -1));
    }

    private (StoredGoalRetentionFact Fact, string Root)[] CreateRecentThree()
    {
        var roots = CreateThree();
        var ages = new[] { 2, 1, 3 };
        for (var index = 0; index < roots.Length; index++)
        {
            EditLease(roots[index].Root, "lastUsedAt", JsonValue.Create(_now.AddHours(-ages[index])));
            File.WriteAllBytes(Path.Combine(roots[index].Root, "payload.bin"), new byte[(index + 1) * 1000]);
        }
        return roots;
    }

    private static long Size(string root) => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Sum(path => new FileInfo(path).Length);

    private (StoredGoalRetentionFact Fact, string Root)[] CreateThree() =>
        new[] { GoalStatus.Completed, GoalStatus.Cancelled, GoalStatus.Superseded }
            .Select((status, index) =>
            {
                var goal = new GoalId($"{index + 1:00000000}000000000000000000000000");
                return (new StoredGoalRetentionFact(goal.Value, status, _now.AddDays(-10)), CreateRoot(goal));
            }).ToArray();

    private string CreateRoot(GoalId goal)
    {
        var root = DotnetBuildEnvironmentManager.CreateAttempt(goal, "acceptance",
            storageRoot: Storage, repositoryRoot: Path.Combine(_temp, "repo")).RootPath;
        File.WriteAllBytes(Path.Combine(root, "payload.bin"), new byte[1000]);
        EditLease(root, "lastUsedAt", JsonValue.Create(_now.AddDays(-10)));
        return root;
    }

    private static void EditLease(string root, string key, JsonNode? value)
    {
        var path = Path.Combine(root, "lease", "lease.json");
        var lease = JsonNode.Parse(File.ReadAllText(path))!;
        lease[key] = value;
        File.WriteAllText(path, lease.ToJsonString());
    }

    private OwnedRunRootRegistry Registry()
    {
        var db = Path.Combine(_temp, "state.db");
        StateDbMigrations.EnsureUpToDate(db);
        return new OwnedRunRootRegistry(db);
    }

    private TerminalBuildRootRetention Retention() => new(Storage, Registry(), _ => false, TimeProvider.System);

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static string[] Snapshot(string root) =>
    [
        .. Directory.GetDirectories(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "directory:" + Path.GetRelativePath(root, path)),
        .. Directory.GetFiles(root, "*", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => "file:" + Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))))
    ];

    public void Dispose()
    {
        while (DotnetBuildEnvironmentManager.DrainOwnedRunRootWriteFailures(25, Storage).Count == 25) { }
        Directory.Delete(_temp, recursive: true);
    }
}
