using System.Collections.Concurrent;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private kernels, repository fakes, clocks and event gates only.
public sealed class ProviderHoldHistoryStartupSeedTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_PendingRepository_EmitsProgressAndMeasuredSummary(bool blockLoad)
    {
        var script = new Script();
        var repository = new Repository();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task Block()
        {
            entered.TrySetResult();
            await release.Task;
        }
        if (blockLoad) repository.BeforeLoad = Block;
        else repository.BeforeRows = Block;
        var kernel = new AgentOrchestratorKernel();
        var summaryWhileActive = false;
        var run = Task.Run(() => ProviderHoldHistorySeed.Apply(kernel, repository, line =>
        {
            summaryWhileActive = !script.Stopped.Task.IsCompleted;
            script.Emit(line);
        }, script.Heartbeat, script.Time));
        try
        {
            await WaitFor(entered.Task, "repository entered");
            Assert.Equal("STARTUP_PROGRESS phase=provider-hold-history-seed elapsed_ms=0", await script.NextLine());
            for (var tick = 1; tick <= 3; tick++)
            {
                var delay = await script.NextDelay();
                script.Time.ElapsedTicks = TimeSpan.FromSeconds(tick * 15).Ticks;
                delay.TrySetResult();
                Assert.Equal($"STARTUP_PROGRESS phase=provider-hold-history-seed elapsed_ms={tick * 15000}",
                    await script.NextLine());
            }
            await script.NextDelay();
            release.TrySetResult();
            await WaitFor(run, "seed return");
            await WaitFor(script.Stopped.Task, "seed ticker cancellation");
            Assert.Equal("PROVIDER_HOLD_HISTORY candidates=1 retained=1 goals=aaaaaaaa elapsedMs=45000 loaded=1",
                await script.NextLine());
            Assert.True(summaryWhileActive);
            Assert.Equal(5, script.Lines.Count);
            Assert.Equal(new[] { Repository.SourceId }, repository.RequestedIds);
            Assert.Equal(Repository.SourceId, Assert.Single(kernel.ProviderHoldHistory.Retained).Id.Value);
        }
        finally
        {
            release.TrySetResult();
            await WaitFor(run, "seed cleanup");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Apply_RepositoryFailure_StopsHeartbeatAndPreservesFailure(bool cancelled)
    {
        var script = new Script();
        Exception failure = cancelled ? new OperationCanceledException("fixture") : new InvalidOperationException("fixture");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var repository = new Repository { BeforeRows = async () => { await release.Task; throw failure; } };
        var kernel = new AgentOrchestratorKernel();
        var run = Task.Run(() => ProviderHoldHistorySeed.Apply(kernel, repository, script.Emit, script.Heartbeat, script.Time));
        try
        {
            await script.NextDelay();
            release.TrySetResult();
            await WaitFor(run, "failed seed return");
            await WaitFor(script.Stopped.Task, "failed seed ticker cancellation");
        }
        finally
        {
            release.TrySetResult();
            await WaitFor(run, "failed seed cleanup");
        }
        Assert.Equal(new[]
        {
            "STARTUP_PROGRESS phase=provider-hold-history-seed elapsed_ms=0",
            $"PROVIDER_HOLD_HISTORY seed-failed: {failure.GetType().Name}: fixture"
        }, script.Lines);
        Assert.Empty(kernel.ProviderHoldHistory.Retained);
        Assert.Empty(repository.RequestedIds);
    }

    [Fact]
    public void Apply_EmptyIndex_LegacyEntryPointRemainsSilent()
    {
        var repository = new Repository { Rows = [] };
        var lines = new List<string>();
        ProviderHoldHistorySeed.Apply(new AgentOrchestratorKernel(), repository, lines.Add);
        Assert.Empty(lines);
        Assert.Empty(repository.RequestedIds);
    }

    [Fact]
    public void Apply_ActiveArchiveHold_PreservesFullLoadRetainedSetAndSchedule()
    {
        var repository = new Repository();
        var oracle = new ProviderHoldHistory();
        oracle.Retain(repository.Stored.Goals, []);
        var kernel = new AgentOrchestratorKernel();
        ProviderHoldHistorySeed.Apply(kernel, repository, _ => { });
        Assert.Equal(oracle.Retained.Select(goal => goal.Id), kernel.ProviderHoldHistory.Retained.Select(goal => goal.Id));
        Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(oracle.Scope([]), "xAI", null, out var expected));
        Assert.True(ProviderLimitSignals.TryGetProviderBudgetExhaustionHold(kernel.ProviderHoldScope, "xAI", null, out var actual));
        Assert.Equal(expected, actual);
        var now = expected.PlacedAt.AddHours(1);
        Assert.Equal(ProviderBudgetHoldSchedules.Evaluate(oracle.Scope([]), expected, now),
            ProviderBudgetHoldSchedules.Evaluate(kernel.ProviderHoldScope, actual, now));
    }

    private static async Task WaitFor(Task task, string eventName)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        catch (TimeoutException exception) { throw new TimeoutException($"Timed out waiting for {eventName}", exception); }
    }

    private sealed class Script
    {
        private readonly Channel<TaskCompletionSource> _delays = Channel.CreateUnbounded<TaskCompletionSource>();
        private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();
        internal readonly ConcurrentQueue<string> Lines = new();
        internal readonly TaskCompletionSource Stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ManualTime Time = new();
        internal readonly ConductorStartupHeartbeat Heartbeat;

        internal Script()
        {
            Heartbeat = new(Emit, () => DateTimeOffset.UnixEpoch.AddTicks(Time.ElapsedTicks), async (interval, token) =>
            {
                Assert.Equal(TimeSpan.FromSeconds(15), interval);
                var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Assert.True(_delays.Writer.TryWrite(release));
                try { await release.Task.WaitAsync(token); }
                catch (OperationCanceledException) { Stopped.TrySetResult(); throw; }
            });
        }

        internal void Emit(string line)
        {
            Lines.Enqueue(line);
            Assert.True(_lines.Writer.TryWrite(line));
        }

        internal async Task<string> NextLine()
        {
            var next = _lines.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
            await WaitFor(next, "emitted line");
            return await next;
        }

        internal async Task<TaskCompletionSource> NextDelay()
        {
            var next = _delays.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask();
            await WaitFor(next, "heartbeat delay request");
            return await next;
        }
    }

    private sealed class ManualTime : TimeProvider
    {
        private long _elapsedTicks;
        internal long ElapsedTicks
        {
            get => Interlocked.Read(ref _elapsedTicks);
            set => Interlocked.Exchange(ref _elapsedTicks, value);
        }
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => ElapsedTicks;
    }

    private sealed class Repository : IOrchestratorStateRepository
    {
        internal const string SourceId = "aaaaaaaa000000000000000000000000";
        internal readonly AgentOrchestratorKernel Stored = new();
        internal readonly List<string> RequestedIds = [];
        internal Func<Task>? BeforeRows;
        internal Func<Task>? BeforeLoad;
        internal IReadOnlyList<ModelFitHistoryRow> Rows;

        internal Repository()
        {
            var at = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
            var source = Stored.CreateGoal(new GoalId(SourceId), "Archive hold",
                [new TaskSpec(new TaskId("source-task"), "Verify fixture", AgentRole.Tester)]);
            Stored.ActivateGoal(source.Id, [new(new("tester"), "Tester", AgentRole.Tester,
                new("xAI", "fixture", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey))]);
            var task = source.Tasks.Single();
            Stored.RecordTaskDispatch(source.Id, task.Id, new("fixture", "fixture", @"C:\fixture", at, ProviderName: "xAI"));
            Stored.RecordTaskVerification(source.Id, task.Id, new("fixture", @"C:\fixture", 1, "", "Provider budget exhausted.",
                at.AddMinutes(1), ProviderFailureKind: ProviderFailureKind.BudgetExhausted, DispatchStartedAt: at));
            Rows = [new(SourceId, task.Id.Value, AgentRole.Tester, "xAI", "fixture", null, null,
                WorkTaskStatus.Failed, ModelFitHistory.Unknown, at.AddMinutes(1), TaskOutcomeRules.ProviderBudgetExhausted.Token)];
        }

        public async Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default)
        {
            if (BeforeRows is { } before) await before();
            return Rows;
        }

        public async Task<AgentOrchestratorKernel> LoadGoalsAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default)
        {
            RequestedIds.AddRange(goalIds.Select(id => id.Value));
            if (BeforeLoad is { } before) await before();
            return Stored;
        }

        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(IReadOnlyCollection<GoalId> goalIds,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(AgentRole role, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
