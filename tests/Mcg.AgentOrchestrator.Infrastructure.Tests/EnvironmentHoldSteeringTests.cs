using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique filesystem root, in-memory stores, injected probe and process seams.
public sealed class EnvironmentHoldSteeringTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void RunningGoal_NoPendingSteer_SkipsWorkerProbe()
    {
        var root = Path.Combine(Path.GetTempPath(), $"environment-steer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var (kernel, goal) = SimpleGoal();
            var task = Assert.Single(goal.Tasks);
            var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
            kernel.RecordTaskDispatch(goal.Id, task.Id, new("codex-cli", "codex exec -", root, now));
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, new(6001, "codex exec -", root,
                Path.Combine(root, "out.log"), Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"), now, null, null));
            Assert.Equal(GoalLifecycleState.Running, GoalLifecycle.ResolveState(goal));
            var store = new InMemoryProgressiveReviewSteeringStore();
            var coordinator = CreateCoordinator(root, store);
            Assert.False(coordinator.HasPendingIntent(goal));
            var runner = new EnvironmentContractTestFixture.Runner();
            var events = new List<string>();
            var starts = 0;
            var driver = MakeDriver(getFacts: _ => new(WorkspaceExists: true),
                dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); });
            driver.EnvironmentGate = new(kernel, "project", root, () => EnvironmentContractTestFixture.Contract,
                runner, _ => EnvironmentContractTestFixture.Environment(), (kind, _, _) => events.Add(kind));
            var ticks = new List<BatchTickSummary>();

            new ConductorBatchLoop(progressiveReviewSteering: coordinator, utcNow: () => now.AddSeconds(1))
                .Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                    maxIterations: 1, watchInterval: TimeSpan.FromSeconds(1),
                    sleepFunc: _ => false, persistGoalTick: (_, _) => { }, onTick: ticks.Add);

            Assert.Single(ticks);
            Assert.Equal(0, runner.Calls);
            Assert.Equal(0, starts);
            Assert.Null(EnvironmentHold.TryRead(goal));
            Assert.Empty(events);
            Assert.Empty(store.Receipts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory(Timeout = 30_000)]
    [Trait("Category", "CrossTick")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task VerifiedGoal_GateHold_SurvivesSteeringAcrossTicks(bool pendingSteer)
    {
        var root = Path.Combine(Path.GetTempPath(), $"environment-steer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
            var (kernel, goal) = SimpleGoal();
            var task = Assert.Single(goal.Tasks);
            PassVerificationAt(kernel, goal, task, now);
            Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal));
            var store = new InMemoryProgressiveReviewSteeringStore();
            if (pendingSteer)
                await store.EnqueueIntentAsync(new("stale-steer", goal.Id.Value, task.Id.Value,
                    AgentRole.Developer.ToString(), "old-round", "glance", "inputs", now,
                    "wrong subsystem", "stay scoped", "stay scoped", now));
            var coordinator = CreateCoordinator(root, store);
            Assert.Equal(pendingSteer, coordinator.HasPendingIntent(goal));
            var runner = new EnvironmentContractTestFixture.Runner();
            var events = new List<string>();
            var workerEnvironments = 0;
            var gateEnvironments = 0;
            var gateStarts = 0;
            var workerStarts = 0;
            var driver = MakeDriver(getFacts: _ => new(WorkspaceExists: true),
                dispatchAndStart: _ => { workerStarts++; return DispatchStartOutcome.Started(); },
                runAcceptance: _ => { gateStarts++; return true; });
            driver.EnvironmentGate = new(kernel, "project", root, () => EnvironmentContractTestFixture.Contract,
                runner, forGate =>
                {
                    if (forGate) gateEnvironments++; else workerEnvironments++;
                    // The old steering guard passes in the worker environment and clears the gate hold.
                    runner.Pass = !forGate;
                    return EnvironmentContractTestFixture.Environment(forGate ? "gate-path" : "worker-path");
                }, (kind, _, _) => events.Add(kind));
            Assert.NotNull(driver.EnvironmentGate.Check(goal, ConductorAutonomyPolicy.Conservative,
                GoalLifecycleState.Verified, forGate: true));
            var initialHold = Assert.IsType<GoalHoldState>(goal.CurrentHold);
            var decisions = goal.Timeline.Count;
            var ticks = new List<BatchTickSummary>();

            new ConductorBatchLoop(progressiveReviewSteering: coordinator, utcNow: () => now.AddSeconds(1))
                .Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                    maxIterations: 2, watchInterval: TimeSpan.FromSeconds(1),
                    sleepFunc: _ => false, persistGoalTick: (_, _) => { }, onTick: ticks.Add);

            Assert.Equal(2, ticks.Count);
            Assert.Equal(0, workerEnvironments);
            Assert.Equal(3, gateEnvironments);
            Assert.Equal(3, runner.Calls);
            Assert.Equal(0, gateStarts);
            Assert.Equal(0, workerStarts);
            Assert.Equal(GoalLifecycleState.Verified, GoalLifecycle.ResolveState(goal));
            var retainedHold = Assert.IsType<GoalHoldState>(goal.CurrentHold);
            Assert.Equal(initialHold.Identity, retainedHold.Identity);
            Assert.Equal(initialHold.StartedAt, retainedHold.StartedAt);
            Assert.Equal(decisions, goal.Timeline.Count);
            Assert.Single(events, kind => kind == "environment-hold");
            Assert.DoesNotContain("environment-hold-cleared", events);
            Assert.Empty(store.Receipts);
            if (pendingSteer)
                Assert.Equal(ProgressiveReviewSteerIntentStatus.Pending, Assert.Single(store.Intents).Status);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static ProgressiveReviewSteeringCoordinator CreateCoordinator(
        string root, IProgressiveReviewSteeringStore store) =>
        new(OrchestratorWorkspace.ForDirectory(root), DefaultAgents(), WorkerProfileCatalog.Default(),
            new InMemoryModelProviderRegistry([]), store, new FakeCollaborationItemStore());

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingSteer_FailingProbe_PreservesWorkerIntentAndEnvironmentHold(bool startupHold)
    {
        var root = Path.Combine(Path.GetTempPath(), $"environment-steer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var now = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
            var (kernel, goal) = SimpleGoal();
            var task = Assert.Single(goal.Tasks);
            var dispatch = new TaskDispatchRecord("codex-cli", "codex exec -", root, now);
            kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
            // Recording enriches a copy; preserve the stored instance across the held tick.
            var recordedDispatch = Assert.IsType<TaskDispatchRecord>(task.LastDispatch);
            var process = new TaskProcessRecord(6001, dispatch.Command, root,
                Path.Combine(root, "out.log"), Path.Combine(root, "err.log"),
                Path.Combine(root, "exit.txt"), now, null, null);
            kernel.RecordTaskProcessStarted(goal.Id, task.Id, process);
            Assert.Equal(AgentRole.Developer, task.RequiredRole);
            Assert.Equal(WorkTaskStatus.Running, task.Status);

            var store = new InMemoryProgressiveReviewSteeringStore();
            var intent = new ProgressiveReviewSteerIntent("environment-steer", goal.Id.Value, task.Id.Value,
                AgentRole.Developer.ToString(), $"{goal.Id.Value}|{task.Id.Value}|{now.UtcTicks}",
                "glance", "inputs", now, "wrong subsystem", "stay scoped", "stay scoped", now);
            await store.EnqueueIntentAsync(intent);
            Assert.Equal(ProgressiveReviewSteerIntentStatus.Pending, Assert.Single(store.Intents).Status);
            var cancellations = 0;
            var starts = 0;
            var coordinator = new ProgressiveReviewSteeringCoordinator(
                OrchestratorWorkspace.ForDirectory(root), DefaultAgents(), WorkerProfileCatalog.Default(),
                new InMemoryModelProviderRegistry([]), store, new FakeCollaborationItemStore(),
                utcNow: () => now.AddSeconds(1), isProcessRunning: _ => false,
                getLineageDescendants: _ => [], readProcessIdentity: _ => null,
                cancelProcess: (k, goalId, taskId) =>
                {
                    cancellations++;
                    var cancelled = process with { CompletedAt = now.AddSeconds(1), WasCancelled = true };
                    k.RecordTaskProcessCancelled(goalId, taskId, cancelled);
                    return cancelled;
                },
                startProcess: (k, goalId, taskId) =>
                {
                    starts++;
                    var restarted = process with { ProcessId = 7001, StartedAt = now.AddSeconds(2) };
                    k.RecordTaskProcessStarted(goalId, taskId, restarted);
                    return restarted;
                },
                prepareFreshDispatch: (k, g, t, _) => k.RecordTaskDispatch(g.Id, t.Id,
                    dispatch with { DispatchedAt = now.AddSeconds(2) }),
                headResolver: _ => "head", capturedHeadIsAncestor: (_, _, _) => true);
            var runner = new EnvironmentContractTestFixture.Runner();
            var events = new List<string>();
            var driver = MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                dispatchAndStart: _ => { starts++; return DispatchStartOutcome.Started(); });
            driver.EnvironmentGate = new(kernel, "project", root, () => EnvironmentContractTestFixture.Contract,
                runner, _ => EnvironmentContractTestFixture.Environment(), (kind, _, _) => events.Add(kind));
            if (startupHold)
            {
                driver.EnvironmentGate.ProbeAtStart([goal]);
                Assert.NotNull(EnvironmentHold.TryRead(goal));
            }
            var callsBeforeTick = runner.Calls;

            new ConductorBatchLoop(progressiveReviewSteering: coordinator, utcNow: () => now.AddSeconds(1))
                .Run(kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(root, "stop"),
                    maxIterations: 1, watchInterval: TimeSpan.FromSeconds(1),
                    sleepFunc: _ => false, persistGoalTick: (_, _) => { });

            Assert.True(runner.Calls > callsBeforeTick);
            Assert.Equal(0, cancellations);
            Assert.Equal(0, starts);
            Assert.Same(process, task.LastProcess);
            Assert.Same(recordedDispatch, task.LastDispatch);
            Assert.Equal(WorkTaskStatus.Running, task.Status);
            Assert.Equal(ProgressiveReviewSteerIntentStatus.Pending, Assert.Single(store.Intents).Status);
            Assert.Empty(store.Receipts);
            var hold = Assert.IsType<EnvironmentHold>(EnvironmentHold.TryRead(goal));
            Assert.Equal("cargo nextest", hold.Tool);
            Assert.Equal("cargo nextest --version", hold.Command);
            Assert.Equal("missing nextest output tail", hold.OutputTail);
            Assert.Single(events, kind => kind == "environment-hold");
            Assert.DoesNotContain("environment-hold-cleared", events);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
