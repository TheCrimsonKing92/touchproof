using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique filesystem root, in-memory stores, injected probe and process seams.
public sealed class EnvironmentHoldSteeringTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
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
