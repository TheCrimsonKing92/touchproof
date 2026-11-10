using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: instance-scoped snapshots, fake loaders/processes and uniquely owned ledgers.
public sealed class ConductorModelBindingSnapshotTests
{
    private const int CrossTickHangFailsafeMilliseconds = 30_000;

    [Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Persistent_hosts_refresh_catalog_before_servicing_each_tick(bool steward)
    {
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(fixture.Root);
        var purpose = steward ? ModelFunctionPurposes.ConductorSteward : ModelFunctionPurposes.ConductorAuthor;
        var source = ConductorSubscriptionRoundExecutorTests.Catalog(purpose);
        var loads = 0;
        var commands = new List<string>();
        Task<WorkerProcessRunResult> Run(WorkerProcessRunRequest request, CancellationToken _)
        {
            commands.Add(request.Command);
            return Task.FromResult(new WorkerProcessRunResult(0, request.Command.StartsWith("codex")
                ? ConductorAuthorModelRoundCatalogTests.CodexAnswer("answer") : "answer", ""));
        }
        ModelFunctionCatalog Load() { loads++; return source; }
        var authorModel = new ClaudeConductorAuthorModelRound(fixture.Receipts, Run, catalogLoader: Load);
        var stewardModel = new ClaudeConductorStewardModelRound(fixture.Receipts, Run,
            new ConductorSubscriptionRoundExecutorTests.EmptyFiles(), (_, _) => true, catalogLoader: Load);
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var lifecycle = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
        var conduct = new ConductEventLogWriter(workspace.ConductEventsLogPath);
        var authorHost = new ConductorAuthorHost(new ConductorAuthorClaimStore(Path.Combine(fixture.Root, "claims.db")),
            CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory), authorModel, intents,
            new SpecRefinerPrecedentStore(workspace.SpecRefinerPrecedentsPath), _ => fixture.Root, lifecycle, conduct);
        var stewardHost = new ConductorStewardHost(new ConductorStewardTriggerStore(Path.Combine(fixture.Root, "triggers.db")),
            new ConductorStewardTriggerDetector(), stewardModel, intents, new AdjudicationEvidenceResolver(fixture.Root),
            _ => 7, _ => fixture.Root, lifecycle, conduct);
        var kernel = new AgentOrchestratorKernel();
        void Tick()
        {
            if (steward) stewardHost.ServiceTick(kernel);
            else authorHost.ServiceTick(kernel);
        }
        Task<string> Dispatch() => steward
            ? stewardModel.DispatchAsync(ConductorSubscriptionRoundExecutorTests.Trigger(), fixture.Root, CancellationToken.None)
            : authorModel.DispatchAsync(ConductorAuthorModelRoundCatalogTests.Input(), fixture.Root, CancellationToken.None);
        try
        {
            Tick();
            Assert.Equal("answer", await Dispatch());
            source = ConductorSubscriptionRoundExecutorTests.Catalog(purpose, false);
            Tick();
            Assert.Equal("answer", await Dispatch());
            Assert.Equal(2, loads);
            Assert.Equal(2, commands.Count);
            Assert.StartsWith("codex exec", commands[0]);
            Assert.StartsWith("claude ", commands[1]);
        }
        finally { authorHost.Stop(); stewardHost.Stop(); }
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task Queued_work_keeps_dispatch_tick_snapshot_after_next_tick_refresh()
    {
        var source = ConductorSubscriptionRoundExecutorTests.Catalog(ModelFunctionPurposes.ConductorAuthor);
        var loads = 0;
        var snapshot = new ConductorModelBindingSnapshot(null, () => { loads++; return source; });
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        snapshot.RefreshBindings();
        var queued = Task.Run(async () =>
        {
            await release.Task;
            return snapshot.Current;
        });
        source = ConductorSubscriptionRoundExecutorTests.Catalog(ModelFunctionPurposes.ConductorAuthor, false);
        snapshot.RefreshBindings();
        Assert.Equal("claude-cli", snapshot.Current.Bindings[0].Subscription!.WorkerProfileName);
        release.SetResult();
        var captured = await queued.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal("codex-cli", captured.Bindings[0].Subscription!.WorkerProfileName);
        Assert.Equal("claude-cli", captured.Bindings[0].SubscriptionFallback!.WorkerProfileName);
        Assert.Equal(2, loads);
    }

    [Theory(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Round_and_fallback_keep_snapshot_while_next_tick_sees_new_binding(bool steward)
    {
        using var fixture = new ConductorAuthorModelRoundCatalogTests.Fixture();
        var purpose = steward ? ModelFunctionPurposes.ConductorSteward : ModelFunctionPurposes.ConductorAuthor;
        var source = ConductorSubscriptionRoundExecutorTests.Catalog(purpose);
        var loads = 0;
        var primaryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = new List<WorkerProcessRunRequest>();
        async Task<WorkerProcessRunResult> Run(WorkerProcessRunRequest request, CancellationToken _)
        {
            requests.Add(request);
            if (requests.Count == 1)
            {
                primaryStarted.SetResult();
                await release.Task;
                return new(1, "", "ERROR: reached your usage limit; try again later due to usage");
            }
            return new(0, request.Command.StartsWith("codex")
                ? ConductorAuthorModelRoundCatalogTests.CodexAnswer("answer") : "answer", "");
        }
        ModelFunctionCatalog Load() { loads++; return source; }
        var author = new ClaudeConductorAuthorModelRound(fixture.Receipts, Run,
            launcherAvailable: _ => true, catalogLoader: Load);
        var stewardRound = new ClaudeConductorStewardModelRound(fixture.Receipts, Run,
            new ConductorSubscriptionRoundExecutorTests.EmptyFiles(), (_, _) => true,
            launcherAvailable: _ => true, catalogLoader: Load);
        IConductorModelBindingRefresh refresh = steward ? stewardRound : author;
        Task<string> Dispatch() => steward
            ? stewardRound.DispatchAsync(ConductorSubscriptionRoundExecutorTests.Trigger(), fixture.Root, CancellationToken.None)
            : author.DispatchAsync(ConductorAuthorModelRoundCatalogTests.Input() with { TryConsumeFallbackAttempt = () => true },
                fixture.Root, CancellationToken.None);
        refresh.RefreshBindings();
        var active = Task.Run(Dispatch);
        await primaryStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        source = ConductorSubscriptionRoundExecutorTests.Catalog(purpose, false);
        // This refresh represents the next tick; active primary/fallback must keep the old catalog.
        refresh.RefreshBindings();
        release.SetResult();
        Assert.Equal("answer", await active.WaitAsync(TimeSpan.FromSeconds(30)));
        Assert.Equal(2, requests.Count);
        Assert.StartsWith("codex exec", requests[0].Command);
        Assert.StartsWith("claude ", requests[1].Command);
        Assert.Equal(2, loads);
        Assert.Equal("answer", await Dispatch());
        Assert.Equal(3, requests.Count);
        Assert.StartsWith("claude ", requests[2].Command);
        Assert.Equal(2, loads); // No launch or fallback reloads inside a tick.
    }

    [Fact(Timeout = CrossTickHangFailsafeMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task Panel_refresh_preserves_active_receipts_and_swaps_providers_next_case()
    {
        using var h = new PanelTestHarness();
        var source = ConductorPanelProviderAvailabilityTests.Catalog();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var loads = 0;
        async Task<PanelProcessResult> Run(WorkerProcessRunRequest request, CancellationToken token)
        {
            started.TrySetResult();
            await release.Task.WaitAsync(token);
            const string prefix = "case_id=";
            var start = request.StandardInput!.IndexOf(prefix, StringComparison.Ordinal) + prefix.Length;
            var id = request.StandardInput[start..].Split('.')[0];
            return new(0, PanelTestHarness.Answer(id), "");
        }
        IReadOnlyList<IConductorPanelJudgeRunner> Load()
        {
            loads++;
            return [new ProviderBoundPanelJudgeRunner(source, ModelFunctionPurposes.PanelJudgeSol, "a", Run),
                new ProviderBoundPanelJudgeRunner(source, ModelFunctionPurposes.PanelJudgeSonnet, "b", Run)];
        }
        var seats = Load();
        var host = new ConductorJudgePanelHost(h.Store, seats[0], seats[1], _ => h.Candidate,
            new ConductEventLogWriter(h.ConductPath), () => h.Time.UtcNow, judgeLoader: Load);
        try
        {
            var first = h.Enqueue("before-refresh");
            host.ServiceTick(h.Kernel);
            var active = host.CurrentCase!;
            await started.Task.WaitAsync(TimeSpan.FromSeconds(30));
            source = ConductorPanelProviderAvailabilityTests.Catalog("codex-cli", "claude-cli");
            host.ServiceTick(h.Kernel);
            Assert.Same(active, host.CurrentCase);
            Assert.Equal(3, loads);
            release.SetResult();
            await PanelTestHarness.Signal(active, "active panel finished after catalog refresh");
            host.ServiceTick(h.Kernel);
            Assert.Equal(PanelJudgmentOutcome.Complete, h.Store.Judgment(first.Id).Outcome);
            Assert.Equal("claude-cli", h.Store.Results(first.Id).Single(result => result.Judge == "a").WorkerProfileName);
            var second = h.Enqueue("after-refresh");
            await h.Complete(host);
            Assert.Equal(PanelJudgmentOutcome.Complete, h.Store.Judgment(second.Id).Outcome);
            Assert.Equal("codex-cli", h.Store.Results(second.Id).Single(result => result.Judge == "a").WorkerProfileName);
            Assert.Equal("claude-cli", h.Store.Results(second.Id).Single(result => result.Judge == "b").WorkerProfileName);
        }
        finally { host.Stop(); }
    }
}
