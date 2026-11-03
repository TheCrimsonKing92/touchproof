using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: only instance-owned snapshots, delegates and event gates; no external I/O.
public sealed class OwnerGoalChangeSummaryRangeReuseTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddDays(1);
    private static readonly string Basis = new('a', 40);
    private static readonly string Candidate = new('b', 40);
    private static readonly string Moved = new('c', 40);
    private static readonly string Next = new('d', 40);
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task MatchingPromotion_FirstLoadSharesOneReadAndSectionValues()
    {
        var goal = Goal();
        var entered = Signal();
        var release = Signal();
        var calls = new ConcurrentQueue<(string, string)>();
        var query = new OwnerGoalChangeSummaryQuery(
            promotion: (_, _) => Task.FromResult<GoalPromotionFact?>(Promotion(goal)),
            changes: async (basis, candidate, token) =>
            {
                calls.Enqueue((basis, candidate));
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                return Files("M src/shared.cs");
            }, mergeBase: (_, _) => throw new InvalidOperationException("Promotion owns the base."));

        var load = query.LoadAsync(goal, Token);
        await TestHangGuard.WaitAsync(entered.Task, "matching promotion read entered");
        release.TrySetResult();
        var summary = await TestHangGuard.WaitAsync(load, "matching promotion load completed");

        AssertSections(summary, ["M src/shared.cs"], ["M src/shared.cs"]);
        Assert.Contains("promotion range", summary.Changed.Source);
        Assert.Equal(new[] { (Basis, Candidate) }, calls.ToArray());
    }

    [Fact]
    public async Task DistinctRanges_PreserveDispatchValuesAndWholeGoalValues()
    {
        var calls = new List<(string, string)>();
        var query = new OwnerGoalChangeSummaryQuery(changes: (basis, candidate, _) =>
        {
            calls.Add((basis, candidate));
            return Task.FromResult(Files(basis == Basis ? "M src/dispatch.cs" : "A docs/whole.md"));
        }, mergeBase: (_, _) => Task.FromResult<string?>(Moved));

        var summary = await query.LoadAsync(Goal(), Token);

        AssertSections(summary, ["M src/dispatch.cs"], ["A docs/whole.md"]);
        Assert.Contains($"Git {Basis}..{Candidate}", summary.Changed.Source);
        Assert.Contains("latest contributing dispatch range, not whole-goal coverage", summary.Changed.Source);
        Assert.Equal(new[] { (Basis, Candidate), (Moved, Candidate) }, calls);
    }

    [Fact]
    public async Task Refresh_ResolvesBasesAgainAndSeparatesChangedCandidates()
    {
        var basis = Basis;
        var baseReads = 0;
        var promotionReads = 0;
        var calls = new List<(string, string)>();
        var query = new OwnerGoalChangeSummaryQuery(
            promotion: (_, _) => { promotionReads++; return Task.FromResult<GoalPromotionFact?>(null); },
            changes: (from, to, _) =>
            {
                calls.Add((from, to));
                return Task.FromResult(Files($"M {from}/{to}.cs"));
            }, mergeBase: (_, _) => { baseReads++; return Task.FromResult<string?>(basis); });

        var first = await query.LoadAsync(Goal(), Token);
        var repeated = await query.LoadAsync(Goal(), Token);
        AssertSections(repeated, first.Changed.Items, first.Preview!.Changes.Files);
        Assert.Single(calls);
        basis = Moved;
        var moved = await query.LoadAsync(Goal(), Token);
        AssertSections(moved, first.Changed.Items, [$"M {Moved}/{Candidate}.cs"]);
        var changed = await query.LoadAsync(Goal(candidate: Next), Token);
        AssertSections(changed, [$"M {Basis}/{Next}.cs"], [$"M {Moved}/{Next}.cs"]);
        Assert.Equal(new[] { (Basis, Candidate), (Moved, Candidate), (Basis, Next), (Moved, Next) }, calls);
        Assert.Equal(4, baseReads);
        Assert.Equal(4, promotionReads);
    }

    [Fact]
    public async Task PromotionRefresh_MovedBaseAndFailureCannotReuseApplicability()
    {
        var goal = Goal();
        var basis = Basis;
        var fail = false;
        var resolutions = 0;
        var calls = new List<(string, string)>();
        var query = new OwnerGoalChangeSummaryQuery(promotion: (_, _) =>
        {
            resolutions++;
            if (fail) throw new IOException("promotion unavailable");
            return Task.FromResult<GoalPromotionFact?>(Promotion(goal, basis));
        }, changes: (from, to, _) =>
        {
            calls.Add((from, to));
            return Task.FromResult(Files($"M {from}.cs"));
        }, mergeBase: (_, _) => throw new InvalidOperationException("No unlanded fallback."));

        await query.LoadAsync(goal, Token);
        await query.LoadAsync(goal, Token);
        Assert.Single(calls);
        basis = Moved;
        var moved = await query.LoadAsync(goal, Token);
        AssertSections(moved, [$"M {Moved}.cs"], [$"M {Moved}.cs"]);
        fail = true;
        var failed = await query.LoadAsync(goal, Token);
        Assert.Equal(OwnerGoalPreviewChangeKind.Unavailable, failed.Preview!.Changes.Kind);
        Assert.Contains("landing unavailable", failed.Landing);
        Assert.Equal(4, resolutions);
        Assert.Equal(new[] { (Basis, Candidate), (Moved, Candidate), (Basis, Candidate) }, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedDiff_RetriesAndValidEmptyDiffIsReusable(bool fault)
    {
        var succeed = false;
        var reads = 0;
        var query = new OwnerGoalChangeSummaryQuery(changes: (_, _, _) =>
        {
            reads++;
            if (succeed) return Task.FromResult(Files());
            return fault ? Task.FromException<CommittedGoalChanges>(new IOException("read failed"))
                : Task.FromResult(new CommittedGoalChanges(false, [], 0, "unavailable"));
        }, mergeBase: (_, _) => Task.FromResult<string?>(Basis));

        for (var i = 0; i < 2; i++)
        {
            var failed = await query.LoadAsync(Goal(), Token);
            Assert.StartsWith("unavailable", failed.Changed.State);
            Assert.Equal(OwnerGoalPreviewChangeKind.Unavailable, failed.Preview!.Changes.Kind);
        }
        Assert.Equal(4, reads);
        succeed = true;
        var empty = await query.LoadAsync(Goal(), Token);
        var repeated = await query.LoadAsync(Goal(), Token);
        Assert.Equal("observed empty committed range", empty.Changed.State);
        Assert.Equal(OwnerGoalPreviewChangeKind.None, empty.Preview!.Changes.Kind);
        AssertSections(repeated, [], []);
        Assert.Equal(5, reads);
    }

    [Fact]
    public async Task OverlappingMatchingLoads_ShareInProgressRead()
    {
        var entered = Signal();
        var release = Signal();
        var reads = 0;
        var query = new OwnerGoalChangeSummaryQuery(changes: async (_, _, token) =>
        {
            Interlocked.Increment(ref reads);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Files("M src/shared.cs");
        }, mergeBase: (_, _) => Task.FromResult<string?>(Basis));

        var first = query.LoadAsync(Goal(), Token);
        await TestHangGuard.WaitAsync(entered.Task, "shared read entered");
        var second = query.LoadAsync(Goal(), Token);
        release.TrySetResult();
        var results = await TestHangGuard.WaitAsync(Task.WhenAll(first, second), "shared loads completed");
        Assert.All(results, summary => AssertSections(summary, ["M src/shared.cs"], ["M src/shared.cs"]));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task CancelledWaiter_DoesNotCancelInitiatingRead()
    {
        var entered = Signal();
        var release = Signal();
        var reads = 0;
        using var waiterToken = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var query = new OwnerGoalChangeSummaryQuery(changes: async (_, _, token) =>
        {
            Interlocked.Increment(ref reads);
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return Files("M src/shared.cs");
        }, mergeBase: (_, _) => Task.FromResult<string?>(Basis));

        var first = query.LoadAsync(Goal(), Token);
        await TestHangGuard.WaitAsync(entered.Task, "initiating read entered");
        var waiter = query.LoadAsync(Goal(), waiterToken.Token);
        waiterToken.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.TrySetResult();
        AssertSections(await TestHangGuard.WaitAsync(first, "initiator completed"),
            ["M src/shared.cs"], ["M src/shared.cs"]);
        await query.LoadAsync(Goal(), Token);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task CancelledInitiator_SharedReadRemainsRetryable()
    {
        var entered = Signal();
        var stopped = Signal();
        var release = Signal();
        var reads = 0;
        using var initiatorToken = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var query = new OwnerGoalChangeSummaryQuery(changes: async (_, _, token) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                try { await release.Task.WaitAsync(token); }
                finally { stopped.TrySetResult(); }
            }
            return Files("M src/retry.cs");
        }, mergeBase: (_, _) => Task.FromResult<string?>(Basis));

        var first = query.LoadAsync(Goal(), initiatorToken.Token);
        await TestHangGuard.WaitAsync(entered.Task, "cancelled initiating read entered");
        var second = query.LoadAsync(Goal(), Token);
        initiatorToken.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        await TestHangGuard.WaitAsync(stopped.Task, "initiating delegate observed cancellation");
        var retried = await query.LoadAsync(Goal(), Token);
        AssertSections(retried, ["M src/retry.cs"], ["M src/retry.cs"]);
        Assert.Equal(2, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DistinctOverlappingLoads_SerializeReadsAndRetryCancelledQueue(bool cancel)
    {
        var entered = Signal();
        var release = Signal();
        var calls = new ConcurrentQueue<(string, string)>();
        var active = 0;
        var maximum = 0;
        using var queuedToken = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var query = new OwnerGoalChangeSummaryQuery(changes: async (basis, candidate, token) =>
        {
            var current = Interlocked.Increment(ref active);
            maximum = Math.Max(maximum, current);
            calls.Enqueue((basis, candidate));
            try
            {
                if (candidate == Candidate)
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return Files($"M {candidate}.cs");
            }
            finally { Interlocked.Decrement(ref active); }
        }, mergeBase: (candidate, _) => Task.FromResult<string?>(candidate == Candidate ? Basis : Moved));

        var first = query.LoadAsync(Goal(), Token);
        await TestHangGuard.WaitAsync(entered.Task, "first serialized read entered");
        var second = query.LoadAsync(Goal(Moved, Next), queuedToken.Token);
        Assert.Single(calls);
        if (cancel)
        {
            queuedToken.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        }
        release.TrySetResult();
        AssertSections(await TestHangGuard.WaitAsync(first, "first serialized load completed"),
            [$"M {Candidate}.cs"], [$"M {Candidate}.cs"]);
        var next = cancel ? await query.LoadAsync(Goal(Moved, Next), Token)
            : await TestHangGuard.WaitAsync(second, "second serialized load completed");
        AssertSections(next, [$"M {Next}.cs"], [$"M {Next}.cs"]);
        Assert.Equal(new[] { (Basis, Candidate), (Moved, Next) }, calls.ToArray());
        Assert.Equal(1, maximum);
        Assert.Equal(0, active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SupersededLoad_LateResolutionCannotEvictNewRange(bool fault)
    {
        var entered = Signal();
        var release = Signal();
        var calls = new List<(string, string)>();
        var pause = true;
        var query = new OwnerGoalChangeSummaryQuery(changes: (basis, candidate, _) =>
        {
            calls.Add((basis, candidate));
            return Task.FromResult(Files($"M {candidate}.cs"));
        }, mergeBase: async (candidate, token) =>
        {
            if (candidate == Candidate && pause)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
                if (fault) throw new IOException("old base resolution failed");
            }
            return candidate == Candidate ? Basis : Moved;
        });

        var old = query.LoadAsync(Goal(), Token);
        await TestHangGuard.WaitAsync(entered.Task, "old whole-goal resolution entered");
        var current = await query.LoadAsync(Goal(Moved, Next), Token);
        release.TrySetResult();
        var prior = await TestHangGuard.WaitAsync(old, "superseded load completed");
        if (fault) Assert.Equal(OwnerGoalPreviewChangeKind.Unavailable, prior.Preview!.Changes.Kind);
        else AssertSections(prior, [$"M {Candidate}.cs"], [$"M {Candidate}.cs"]);
        var repeated = await query.LoadAsync(Goal(Moved, Next), Token);
        AssertSections(repeated, current.Changed.Items, current.Preview!.Changes.Files);
        Assert.Equal(new[] { (Basis, Candidate), (Moved, Next) }, calls);
        pause = false;
        await query.LoadAsync(Goal(), Token);
        Assert.Equal(new[] { (Basis, Candidate), (Moved, Next), (Basis, Candidate) }, calls);
    }

    [Fact]
    public async Task HistoricalRanges_AreEvictedAcrossGoalsAndUnavailableSelection()
    {
        var calls = new List<(string, string)>();
        var query = new OwnerGoalChangeSummaryQuery(changes: (basis, candidate, _) =>
        {
            calls.Add((basis, candidate));
            return Task.FromResult(Files($"M {candidate}.cs"));
        }, mergeBase: (_, _) => Task.FromResult<string?>(Basis));
        for (var i = 1; i <= 20; i++)
        {
            var candidate = i.ToString("x40");
            var summary = await query.LoadAsync(Goal(candidate: candidate, id: $"range-{i}"), Token);
            AssertSections(summary, [$"M {candidate}.cs"], [$"M {candidate}.cs"]);
        }
        Assert.Equal(20, calls.Count);
        await query.LoadAsync(Goal(candidate: 1.ToString("x40")), Token);
        Assert.Equal(21, calls.Count);
        await query.LoadAsync(Goal(candidate: null), Token);
        await query.LoadAsync(Goal(candidate: 1.ToString("x40")), Token);
        Assert.Equal(22, calls.Count);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static CommittedGoalChanges Files(params string[] files) => new(true, files, files.Length);

    private static void AssertSections(OwnerGoalChangeSummary summary,
        IReadOnlyList<string> observed, IReadOnlyList<string> whole)
    {
        Assert.Equal(observed, summary.Changed.Items);
        Assert.Equal(whole, summary.Preview!.Changes.Files);
        Assert.Equal(whole.Count, summary.Preview.Changes.Total);
        Assert.Equal(whole.Count == 0 ? OwnerGoalPreviewChangeKind.None : OwnerGoalPreviewChangeKind.Listed,
            summary.Preview.Changes.Kind);
    }

    private static GoalPromotionFact Promotion(Goal goal, string? basis = null) => new(goal.Id.Value,
        Candidate, basis ?? Basis, Next, At.AddMinutes(1), GoalPromotionFact.SoloProvenance);

    private static Goal Goal(string? basis = null, string? candidate = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        string id = "range-reuse")
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId(id), "Range reuse", [new(TaskId.New(), "Develop", AgentRole.Developer)]);
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        var dispatch = new TaskDispatchSnapshot("developer", "range", "fixture", At,
            BaseCommit: basis ?? Basis, ResultCommit: candidate);
        return AgentOrchestratorKernel.FromSnapshot(new([snapshot with
        {
            Tasks = snapshot.Tasks.Select(task => task with
            {
                LastDispatch = dispatch, DispatchHistory = [dispatch]
            }).ToArray()
        }], [])).Goals.Single();
    }
}
