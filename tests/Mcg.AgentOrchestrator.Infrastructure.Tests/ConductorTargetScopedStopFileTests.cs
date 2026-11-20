using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its kernel and lease directory. Console capture is flow-local.
public sealed class ConductorTargetScopedStopFileTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output), IDisposable
{
    private const string StopContents = "stop";
    private const string IgnoredNotice = "is ignored for named goal";
    private readonly string _root = SharedTestSupport.CreateTempDirectory();
    private string StopFile => Path.Combine(_root, ConductorBatchLoop.StopFileName);

    [Fact]
    public void TargetScopedRun_PreExistingStopFile_TicksAndDispatchesNamedGoal()
    {
        var (kernel, goal) = SimpleGoal("named goal");
        var otherGoal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "other goal");
        File.WriteAllText(StopFile, StopContents);
        var initialWriteTime = File.GetLastWriteTimeUtc(StopFile);
        var dispatches = new List<GoalId>();
        var driver = DispatchingDriver(kernel, candidate => dispatches.Add(candidate.Id));

        var summary = new ConductorBatchLoop().Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, StopFile,
            maxIterations: 1, onlyGoalId: goal.Id.Value);

        Assert.Equal(1, summary.Ticks);
        Assert.False(summary.StopRequested);
        Assert.Equal("max-iter", summary.StopReason);
        Assert.Equal(goal.Id, Assert.Single(dispatches));
        var dispatchedTask = kernel.GetTask(goal.Id, goal.Tasks.Single().Id);
        Assert.Equal(WorkTaskStatus.Running, dispatchedTask.Status);
        Assert.NotNull(dispatchedTask.LastDispatch);
        Assert.Equal(WorkTaskStatus.Assigned, kernel.GetTask(otherGoal.Id, otherGoal.Tasks.Single().Id).Status);
        Assert.Equal(StopContents, File.ReadAllText(StopFile));
        Assert.Equal(initialWriteTime, File.GetLastWriteTimeUtc(StopFile));
    }

    [Fact]
    public void BoardRun_PreExistingStopFile_StopsBeforeAnyDispatchAndDetaches()
    {
        var (kernel, runningGoal) = SimpleGoal("running goal");
        var runningTask = runningGoal.Tasks.Single();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "ready goal");
        var now = DateTimeOffset.UtcNow;
        kernel.RecordTaskDispatch(runningGoal.Id, runningTask.Id, new TaskDispatchRecord("worker", "worker.exe", _root, now));
        kernel.RecordTaskProcessStarted(runningGoal.Id, runningTask.Id, new TaskProcessRecord(
            444, "worker.exe", _root, Path.Combine(_root, "stdout"), Path.Combine(_root, "stderr"),
            Path.Combine(_root, "exit"), now, null, null, OwnedProcessIds: [444]));
        File.WriteAllText(StopFile, StopContents);
        var dispatches = new List<GoalId>();
        var driver = DispatchingDriver(kernel, candidate => dispatches.Add(candidate.Id));
        using var registry = WorkerProcessJobs.UseRegistryScopeForTests(dbPath: null);
        var runner = new BackgroundDispatchRunner(
            isStillRunning: _ => false,
            tryKillOwnedProcess: _ => throw new InvalidOperationException("Graceful detach attempted to kill a worker."));
        var loop = new ConductorBatchLoop(detachGoalRunningDispatches: (state, candidate) =>
            runner.DetachRunningProcessesForGoal(state, candidate.Id));
        BatchLoopSummary? summary = null;

        var console = AsyncLocalConsoleRouter.Capture(() => summary = loop.Run(
            kernel, driver, ConductorAutonomyPolicy.Conservative, StopFile, maxIterations: 1));

        Assert.NotNull(summary);
        Assert.Equal(0, summary.Ticks);
        Assert.True(summary.StopRequested);
        Assert.Equal("stop-file", summary.StopReason);
        Assert.Empty(dispatches);
        var detachedTask = kernel.GetTask(runningGoal.Id, runningTask.Id);
        Assert.Equal(WorkTaskStatus.Running, detachedTask.Status);
        Assert.NotNull(detachedTask.LastProcess);
        Assert.True(detachedTask.LastProcess.WasGracefullyDetachedByConductor);
        Assert.False(detachedTask.LastProcess.WasCancelled);
        Assert.Contains("no new dispatches will be started", console, StringComparison.Ordinal);
        Assert.DoesNotContain(IgnoredNotice, console, StringComparison.Ordinal);
    }

    [Fact]
    public void TargetScopedRun_StopFileCreatedAfterStart_StopsAtNextCheck()
    {
        Assert.False(File.Exists(StopFile));

        AssertStopsAfterFirstTick(() => File.WriteAllText(StopFile, StopContents));
    }

    [Fact]
    public void TargetScopedRun_PreExistingStopFileRewrittenAfterStart_StopsAtNextCheck()
    {
        File.WriteAllText(StopFile, StopContents);
        // Backdate the file so a same-content rewrite has a distinct stamp even on coarse filesystems.
        File.SetLastWriteTimeUtc(StopFile, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        AssertStopsAfterFirstTick(() => File.WriteAllText(StopFile, StopContents));

        Assert.Equal(StopContents, File.ReadAllText(StopFile));
    }

    [Fact]
    public void TargetScopedRun_PreExistingStopFile_PrintsSingleIgnoredNotice()
    {
        var (kernel, goal) = SimpleGoal();
        File.WriteAllText(StopFile, StopContents);
        var policy = ConductorAutonomyPolicy.Conservative;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            getRunningCount: () => policy.MaxConcurrentPaidWorkers);
        using var wakeSignal = new TestWakeSignal();
        BatchLoopSummary? summary = null;

        var console = AsyncLocalConsoleRouter.Capture(() => summary = new ConductorBatchLoop().Run(
            kernel, driver, policy, StopFile,
            maxIterations: 2, watchInterval: TimeSpan.FromSeconds(1), wakeSignal: wakeSignal,
            onlyGoalId: goal.Id.Value, keepAliveWhenIdle: true));

        Assert.NotNull(summary);
        Assert.Equal(2, summary.Ticks);
        Assert.False(summary.StopRequested);
        Assert.NotEmpty(wakeSignal.Timeouts);
        var notice = Assert.Single(console.Split('\n'), line => line.Contains(IgnoredNotice, StringComparison.Ordinal));
        Assert.Contains("repo-wide stop file", notice, StringComparison.Ordinal);
        Assert.Contains(goal.Id.Value[..8], notice, StringComparison.Ordinal);
    }

    private void AssertStopsAfterFirstTick(Action requestStop)
    {
        var (kernel, goal) = SimpleGoal();
        var ticks = new List<int>();

        var summary = new ConductorBatchLoop().Run(
            kernel, DispatchingDriver(kernel), ConductorAutonomyPolicy.Conservative, StopFile,
            maxIterations: 2, onTick: tick =>
            {
                ticks.Add(tick.Tick);
                requestStop();
            }, onlyGoalId: goal.Id.Value);

        Assert.Equal(1, Assert.Single(ticks));
        Assert.Equal(1, summary.Ticks);
        Assert.True(summary.StopRequested);
        Assert.Equal("stop-file", summary.StopReason);
    }

    private ConductorDriver DispatchingDriver(AgentOrchestratorKernel kernel, Action<Goal>? onDispatch = null) =>
        MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: goal =>
            {
                onDispatch?.Invoke(goal);
                kernel.RecordTaskDispatch(goal.Id, goal.Tasks.Single().Id,
                    new TaskDispatchRecord("worker", "worker.exe", _root, DateTimeOffset.UtcNow));
                return DispatchStartOutcome.Started();
            },
            executionDirectory: _root);

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_root);
}
