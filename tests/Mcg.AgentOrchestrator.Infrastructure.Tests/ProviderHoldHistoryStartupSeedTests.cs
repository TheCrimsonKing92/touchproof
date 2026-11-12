using System.Collections.Concurrent;
using System.Threading.Channels;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private kernels, repository fakes, clocks and event gates only.
public sealed class ProviderHoldHistoryStartupSeedTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Apply_PendingRepository_EmitsProgressAndMeasuredSummary(int stage)
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
        if (stage == 2) repository.BeforeLoad = Block;
        else if (stage == 1) repository.BeforeEvidence = Block;
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
            Assert.Equal($"PROVIDER_HOLD_HISTORY candidates=1 retained=1 goals=aaaaaaaa elapsedMs=45000 loaded=1 historyMs={(stage == 0 ? 45000 : 0)} evidenceMs={(stage == 1 ? 45000 : 0)} pruneMs=0",
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_RecoveredProvider_PrunesAll73CandidatesBeforeFullLoad(bool compressed)
    {
        var repository = new Repository(empty: true) { Compressed = compressed };
        for (var index = 0; index < 73; index++)
            repository.AddObservation(index.ToString("x32"), index, success: index >= 8);
        var oracle = new ProviderHoldHistory();
        oracle.Retain(repository.Stored.Goals, []);
        var kernel = new AgentOrchestratorKernel();
        var lines = new List<string>();
        ProviderHoldHistorySeed.Apply(kernel, repository, lines.Add);
        Assert.Equal(73, repository.EvidenceIds.Count);
        Assert.Empty(repository.RequestedIds);
        Assert.Empty(oracle.Retained);
        Assert.Equal(oracle.Retained.Select(goal => goal.Id), kernel.ProviderHoldHistory.Retained.Select(goal => goal.Id));
        Assert.Matches(@"^PROVIDER_HOLD_HISTORY candidates=73 retained=0 goals= elapsedMs=\d+ loaded=0 historyMs=\d+ evidenceMs=\d+ pruneMs=\d+$", Assert.Single(lines));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apply_EvidenceReadFailure_LoadsAllCandidatesAndPreservesOracle(bool recovered)
    {
        var repository = new Repository
        {
            BeforeEvidence = () => Task.FromException(new JsonException("Malformed sparse evidence."))
        };
        repository.AddObservation("bbbbbbbb000000000000000000000000", 1, success: recovered);
        var candidates = ProviderHoldHistory.SeedCandidates(repository.Rows, new HashSet<string>(StringComparer.Ordinal));
        var oracle = new ProviderHoldHistory();
        oracle.Retain(repository.Stored.Goals, []);
        if (recovered) Assert.Empty(oracle.Retained);
        else Assert.NotEmpty(oracle.Retained);
        var kernel = new AgentOrchestratorKernel();
        var lines = new List<string>();

        ProviderHoldHistorySeed.Apply(kernel, repository, lines.Add);

        Assert.Equal(candidates.OrderBy(id => id), repository.EvidenceIds.OrderBy(id => id));
        Assert.Equal(candidates.OrderBy(id => id), repository.RequestedIds.OrderBy(id => id));
        Assert.Equal(oracle.Retained.Select(goal => goal.Id), kernel.ProviderHoldHistory.Retained.Select(goal => goal.Id));
        Assert.Equal(ProviderUsageHoldView.Read(oracle.Scope([]), DateTimeOffset.UnixEpoch),
            ProviderUsageHoldView.Read(kernel.ProviderHoldScope, DateTimeOffset.UnixEpoch));
        Assert.Matches(@"^PROVIDER_HOLD_HISTORY candidates=2 retained=\d+ goals=.* elapsedMs=\d+ loaded=2 historyMs=\d+ evidenceMs=\d+ pruneMs=\d+$", Assert.Single(lines));
    }

    [Fact]
    public void Apply_EvidenceReadCancellation_PreservesFailureWithoutFullLoad()
    {
        var repository = new Repository
        {
            BeforeEvidence = () => Task.FromException(new OperationCanceledException("Evidence read cancelled."))
        };
        var kernel = new AgentOrchestratorKernel();
        var lines = new List<string>();

        ProviderHoldHistorySeed.Apply(kernel, repository, lines.Add);

        Assert.Single(repository.EvidenceIds);
        Assert.Empty(repository.RequestedIds);
        Assert.Empty(kernel.ProviderHoldHistory.Retained);
        Assert.Equal("PROVIDER_HOLD_HISTORY seed-failed: OperationCanceledException: Evidence read cancelled.", Assert.Single(lines));
    }

    [Fact]
    public void Apply_RecoveryPrunesPreviouslyRetainedSourceToo()
    {
        var repository = new Repository();
        var kernel = new AgentOrchestratorKernel();
        kernel.ProviderHoldHistory.Retain(repository.Stored.Goals, []);
        Assert.Single(kernel.ProviderHoldHistory.Retained);
        repository.AddObservation("bbbbbbbb000000000000000000000000", 1, success: true);
        ProviderHoldHistorySeed.Apply(kernel, repository, _ => { });
        Assert.Empty(kernel.ProviderHoldHistory.Retained);
        Assert.Empty(repository.RequestedIds);
    }

    [Fact]
    public void Apply_CredentialMismatchRetainsOriginalFullLoadAnswer()
    {
        var repository = new Repository();
        repository.AddObservation("bbbbbbbb000000000000000000000000", 1, true, @"C:\other-login");
        var oracle = new ProviderHoldHistory();
        oracle.Retain(repository.Stored.Goals, []);
        var kernel = new AgentOrchestratorKernel();
        ProviderHoldHistorySeed.Apply(kernel, repository, _ => { });
        Assert.Equal(2, repository.RequestedIds.Count);
        Assert.Equal(oracle.Retained.Select(goal => goal.Id), kernel.ProviderHoldHistory.Retained.Select(goal => goal.Id));
        Assert.Equal(ProviderUsageHoldView.Read(oracle.Scope([]), DateTimeOffset.UnixEpoch),
            ProviderUsageHoldView.Read(kernel.ProviderHoldScope, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Apply_RecoveredProviderDoesNotPruneAnotherProvidersHold()
    {
        var repository = new Repository();
        repository.AddObservation("bbbbbbbb000000000000000000000000", 1, true);
        repository.AddObservation("cccccccc000000000000000000000000", 2, false, provider: "Anthropic");
        var oracle = new ProviderHoldHistory();
        oracle.Retain(repository.Stored.Goals, []);
        var kernel = new AgentOrchestratorKernel();
        ProviderHoldHistorySeed.Apply(kernel, repository, _ => { });
        Assert.Equal(3, repository.RequestedIds.Count);
        Assert.Single(oracle.Retained);
        Assert.Equal(oracle.Retained.Select(goal => goal.Id), kernel.ProviderHoldHistory.Retained.Select(goal => goal.Id));
        Assert.Equal(ProviderUsageHoldView.Read(oracle.Scope([]), DateTimeOffset.UnixEpoch),
            ProviderUsageHoldView.Read(kernel.ProviderHoldScope, DateTimeOffset.UnixEpoch));
    }

    [Fact]
    public void Apply_StepDurations_ExcludeGoalLoadAndFitWithinElapsed()
    {
        var time = new ManualTime();
        Task Advance(int milliseconds)
        {
            time.ElapsedTicks += TimeSpan.FromMilliseconds(milliseconds).Ticks;
            return Task.CompletedTask;
        }
        var repository = new Repository
        {
            BeforeRows = () => Advance(100),
            BeforeEvidence = () => Advance(250),
            BeforeLoad = () => Advance(1000)
        };
        var lines = new List<string>();
        ProviderHoldHistoryStartupSeed.Apply(new(), repository, lines.Add, timeProvider: time);
        var line = Assert.Single(lines);
        Assert.EndsWith("elapsedMs=1350 loaded=1 historyMs=100 evidenceMs=250 pruneMs=0", line);
        var fields = line.Split(' ').Where(field => field.Contains('='))
            .Select(field => field.Split('=')).ToDictionary(field => field[0], field => field[1]);
        long Milliseconds(string key) => long.Parse(fields[key], System.Globalization.CultureInfo.InvariantCulture);
        Assert.True(Milliseconds("historyMs") + Milliseconds("evidenceMs") + Milliseconds("pruneMs") <= Milliseconds("elapsedMs"));
        Assert.Single(repository.RequestedIds);
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
        internal readonly List<string> EvidenceIds = [];
        internal Func<Task>? BeforeRows;
        internal Func<Task>? BeforeEvidence;
        internal Func<Task>? BeforeLoad;
        internal IReadOnlyList<ModelFitHistoryRow> Rows;
        internal bool Compressed;

        internal Repository(bool empty = false)
        {
            Rows = [];
            if (!empty) AddObservation(SourceId, 0, success: false);
        }

        internal void AddObservation(string id, int index, bool success, string? credential = null, string provider = "xAI")
        {
            var at = DateTimeOffset.Parse("2026-10-17T11:07:00Z").AddMinutes(index * 10);
            var source = Stored.CreateGoal(new GoalId(id), "Archive hold",
                [new TaskSpec(new TaskId(id), "Verify fixture", AgentRole.Tester)]);
            Stored.ActivateGoal(source.Id, [new(new("tester"), "Tester", AgentRole.Tester,
                new(provider, "fixture", ModelCapability.Text | ModelCapability.Code | ModelCapability.ToolUse, SubscriptionMode.ApiKey))]);
            var task = source.Tasks.Single();
            Stored.RecordTaskDispatch(source.Id, task.Id, new("fixture", "fixture", @"C:\fixture", at,
                ProviderName: provider, ModelName: "fixture", ClaudeCredentialSourceDirectory: credential));
            Stored.RecordTaskVerification(source.Id, task.Id, new("fixture", @"C:\fixture", success ? 0 : 1,
                "", success ? "" : "Provider budget exhausted.", at.AddMinutes(1),
                ProviderFailureKind: success ? ProviderFailureKind.Unknown : ProviderFailureKind.BudgetExhausted,
                DispatchStartedAt: at, CompletionVerdictVerifiedSuccess: success));
            Stored.ReportTaskProgress(source.Id, task.Id, success ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, "Fixture outcome.");
            Rows = ModelFitHistory.FromGoals(Stored.Goals);
        }

        public async Task<IReadOnlyList<ProviderUsageGoalEvidence>?> ReadProviderHoldSeedEvidenceAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default)
        {
            EvidenceIds.AddRange(goalIds.Select(id => id.Value));
            if (BeforeEvidence is { } before) await before();
            var options = new JsonSerializerOptions();
            options.Converters.Add(new JsonStringEnumConverter());
            var result = new List<ProviderUsageGoalEvidence>();
            foreach (var snapshot in Stored.ExportSnapshot().Goals.Where(goal => goalIds.Any(id => id.Value == goal.Id)))
            {
                var json = JsonSerializer.Serialize(snapshot, options);
                result.Add(await ProviderHoldSeedEvidenceReader.ProjectAsync(snapshot.Id,
                    Compressed ? GoalSnapshotArchiveCodec.Encode(json) : (object)json, options, cancellationToken));
            }
            return result;
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
