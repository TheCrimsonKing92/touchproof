using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.CliProcessEnvironment)]
public sealed class CliCommandTestsPersistentRunnerLoadCountersAcceptance : CliCommandTestBase
{
    [Xunit.Fact(DisplayName = "Real tick reload hydrates three open goals and checks only their terminal dependencies")]
    public void RealReloadCountsOpenAndTerminalPopulation()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var (repository, openIds, dependencyIds) = Seed(workspace);
            var kernel = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            Assert.Equal(3, kernel.Goals.Count);
            AgentOrchestratorKernel? reloaded = null;
            var loop = new ConductorBatchLoop(measuredSweep: _ =>
            {
                reloaded = ConductorTickStepLedger.Measure("kernel-reload", () =>
                    CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root));
                return null;
            }, processCpuTime: () => TimeSpan.Zero);

            var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, ConductorBatchLoopTestsStepLedger.FixtureDriver());

            AssertPopulation(ConductorBatchLoopTestsStepLedger.Phase(tick, "sweep"));
            Assert.NotNull(reloaded);
            Assert.Equal(openIds.Order(StringComparer.Ordinal), reloaded.Goals.Select(goal => goal.Id.Value).Order(StringComparer.Ordinal));
            Assert.Equal(5, reloaded.KnownDependencyGoalStatuses.Count);
            Assert.Equal(dependencyIds.Order(StringComparer.Ordinal), reloaded.KnownDependencyGoalStatuses
                .Where(pair => pair.Value == nameof(GoalStatus.Completed))
                .Select(pair => pair.Key.Value).Order(StringComparer.Ordinal));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Xunit.Fact(DisplayName = "Production conduct reload and sweep call sites emit current tick load counters")]
    public void ProductionConductEmitsLoadCounters()
    {
        var root = CreateTempDirectory();
        try
        {
            var workspace = CreateRefinedWorkspace(root);
            var (repository, openIds, _) = Seed(workspace);
            var kernel = CliPersistentStateRunner.LoadConductLoopKernel(repository, executionDirectory: root);
            var baselines = openIds.ToDictionary(id => id, id =>
                Assert.IsType<GoalSnapshot>(repository.LoadGoalAsync(new GoalId(id)).GetAwaiter().GetResult()));
            var checkpoints = new List<GoalSnapshotCheckpointResult>();
            var reloadCalls = 0;
            var context = new CliExecutionContext(kernel, workspace, new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), currentGoal: null,
                reloadKernelForGoals: ids =>
                {
                    reloadCalls++;
                    return CliPersistentStateRunner.LoadConductLoopKernel(repository, ids, root);
                },
                checkpointGoalKernel: (current, ids) =>
                {
                    var snapshots = OrchestratorSnapshot.Capture(current, includeDeferredRows: false)
                        .Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
                    var requested = ids.Select(id => new GoalSnapshotSaveRequest(baselines[id.Value], snapshots[id.Value])).ToArray();
                    var results = repository.CheckpointGoalSnapshotsAsync(requested).GetAwaiter().GetResult();
                    checkpoints.AddRange(results);
                    foreach (var result in results)
                    {
                        if (result.SaveResult?.PersistedSnapshot is { } persisted)
                            baselines[result.GoalId] = persisted;
                    }
                    return results;
                }) { CleanupContext = CreateIsolatedCleanupContext(workspace) };

            // Manual policy keeps all dispatch/model boundaries closed in this store fixture.
            var text = CaptureConsole(() => CliCommandHandlers.Execute(
                ["conduct", "--loop", "--policy", "Manual", "--max-iterations", "1"], context));

            var sweep = Assert.Single(text.Split('\n'), line => line.StartsWith("PHASE_TIMING tick=1 phase=sweep ", StringComparison.Ordinal));
            AssertPopulation(sweep);
            Assert.Equal(1, reloadCalls);
            Assert.Matches(@"kernel-reload:\d+:1(?:,| )", sweep);
            Assert.Matches(@"refresh-dispatches:\d+:3(?:,| )", sweep);
            Assert.Matches(@"terminal-sweep-run:\d+:[12](?:,| )", sweep);
            Assert.Matches(@"owned-root-reap:\d+:[12](?:,| )", sweep);
            Assert.Matches(@" cpu_ms=\d+ ts=\S+\s*$", sweep);
            Assert.Equal(3, context.Kernel.Goals.Count);
            Assert.Contains(checkpoints, checkpoint =>
                checkpoint.SaveResult?.Disposition == GoalSnapshotSaveDisposition.Saved &&
                checkpoint.SaveResult.PersistedSnapshot!.Timeline.Any(entry =>
                    entry.Kind == ProgressKind.GoalPolicyDecision &&
                    entry.Message.StartsWith("Batch loop tick 1: ", StringComparison.Ordinal)));

            var counters = Assert.Single(text.Split('\n'), line =>
                line.StartsWith("TICK_LOAD_COUNTERS tick=1 ", StringComparison.Ordinal));
            const string expectedCounters = "TICK_LOAD_COUNTERS tick=1 " +
                "goal_snapshot_deserialized_utf8_bytes=unknown goal_snapshot_serialized_utf8_bytes=unknown";
            Assert.StartsWith(expectedCounters + " ts=", counters);
            var logOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
            var logged = Assert.Single(File.ReadLines(workspace.ConductEventsLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, logOptions)!),
                entry => entry.EventKind == "tick-load-counters");
            Assert.Equal(expectedCounters, logged.Detail);
            var events = new SqliteRunEventStore(workspace.RunEventStorePath).ReadSinceAsync().GetAwaiter().GetResult();
            var tick = Assert.Single(events, entry => entry.EventType == RunEventTypes.ConductorTick);
            using var payload = JsonDocument.Parse(tick.PayloadJson!);
            Assert.Equal(1, payload.RootElement.GetProperty("Tick").GetInt32());
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("goal_snapshot_deserialized_utf8_bytes").ValueKind);
            Assert.Equal(JsonValueKind.Null, payload.RootElement.GetProperty("goal_snapshot_serialized_utf8_bytes").ValueKind);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task RestartAndOverlappingTicksKeepSeparateReceipts()
    {
        using var firstStore = new GoalTaskHistoryTestFixture();
        using var secondStore = new GoalTaskHistoryTestFixture();
        var firstSeed = GoalTaskHistoryTestFixture.Snapshot(entries: 1);
        var secondSeed = firstSeed with { Objective = "Second tick writes a different payload café" };
        var firstInSweep = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondWrote = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseLateRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lateReadFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? lateRead = null;
        var sweepCalls = 0;
        var checkpoints = 0;
        var kernel = new AgentOrchestratorKernel();
        GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, "Quiet checkpoint");
        var firstLoop = new ConductorBatchLoop(measuredSweep: _ =>
        {
            sweepCalls++;
            var pendingRead = GoalSnapshotByteAccounting.TrackDeferredSnapshotWork(maySerialize: false);
            lateRead = Task.Run(async () =>
            {
                using var observation = pendingRead;
                await WaitFor(releaseLateRead.Task, "first receipt published");
                var loaded = await firstStore.Repository.LoadGoalsAsync([new GoalId(firstSeed.Id)]);
                Assert.Equal(firstSeed.Id, Assert.Single(loaded.Goals).Id.Value);
                lateReadFinished.SetResult();
            });
            firstInSweep.SetResult();
            WaitFor(secondWrote.Task, "second tick wrote").GetAwaiter().GetResult();
            return null;
        });

        var firstRun = Task.Run(() =>
        {
            BatchTickSummary? receipt = null;
            firstLoop.Run(kernel, ConductorBatchLoopTestsStepLedger.FixtureDriver(), ConductorAutonomyPolicy.Manual,
                Path.Combine(Path.GetDirectoryName(firstStore.DatabasePath)!, "stop"), maxIterations: 1,
                quiet: true, onTick: tick => receipt = tick, persistTick: _ =>
                {
                    if (++checkpoints == 1)
                        firstStore.Repository.SaveGoalSnapshotsAsync([firstSeed]).GetAwaiter().GetResult();
                });
            return Assert.IsType<BatchTickSummary>(receipt);
        });
        var secondRun = Task.Run(() => ObserveSecondStore(() =>
        {
            WaitFor(firstInSweep.Task, "first tick entered sweep").GetAwaiter().GetResult();
            secondStore.Repository.SaveGoalSnapshotsAsync([secondSeed]).GetAwaiter().GetResult();
            secondWrote.SetResult();
            WaitFor(lateReadFinished.Task, "late read finished during second tick").GetAwaiter().GetResult();
        }));

        BatchTickSummary first;
        try
        {
            first = await WaitForReceipt(firstRun, "first receipt");
        }
        finally
        {
            releaseLateRead.TrySetResult();
        }
        var second = await WaitForReceipt(secondRun, "second receipt");
        await WaitFor(lateRead!, "late read task");

        Assert.Equal(1, sweepCalls);
        Assert.True(checkpoints > 0);
        Assert.Equal((1, 0, 0), (first.Tick, first.Advanced, first.Done));
        Assert.Null(first.GoalSnapshotDeserializedUtf8Bytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(firstStore.RawGoal(firstSeed.Id)), first.GoalSnapshotSerializedUtf8Bytes);
        Assert.Equal(1, second.Tick);
        Assert.Equal(0, second.GoalSnapshotDeserializedUtf8Bytes);
        Assert.Equal(Encoding.UTF8.GetByteCount(secondStore.RawGoal(secondSeed.Id)), second.GoalSnapshotSerializedUtf8Bytes);
        Assert.NotEqual(first.GoalSnapshotSerializedUtf8Bytes, second.GoalSnapshotSerializedUtf8Bytes);

        var restarted = ObserveSecondStore(() => Assert.Single(
            secondStore.Repository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult()));
        Assert.Equal(1, restarted.Tick);
        Assert.Equal(0, restarted.GoalSnapshotDeserializedUtf8Bytes);
        Assert.Equal(0, restarted.GoalSnapshotSerializedUtf8Bytes);

        BatchTickSummary ObserveSecondStore(Action observe)
        {
            BatchTickSummary? receipt = null;
            new ConductorBatchLoop().Run(new AgentOrchestratorKernel(), ConductorBatchLoopTestsStepLedger.FixtureDriver(),
                ConductorAutonomyPolicy.Manual, Path.Combine(Path.GetDirectoryName(secondStore.DatabasePath)!, "stop"),
                maxIterations: 1, watchInterval: TimeSpan.Zero, onTick: tick => receipt = tick, observeTickWithoutDispatch: observe);
            return Assert.IsType<BatchTickSummary>(receipt);
        }

        static async Task WaitFor(Task task, string signal)
        {
            try { await task.WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (TimeoutException error) { throw new TimeoutException($"Missing signal: {signal}", error); }
        }

        static async Task<BatchTickSummary> WaitForReceipt(Task<BatchTickSummary> task, string signal)
        {
            await WaitFor(task, signal);
            return await task;
        }
    }

    private static void AssertPopulation(string line) => Assert.Contains(
        "load_counters=goals_hydrated=3,metadata_rows=3,terminal_journal_stats=2,", line);

    private static (SqliteOrchestratorStateRepository Repository, string[] OpenIds, string[] DependencyIds) Seed(OrchestratorWorkspace workspace)
    {
        var kernel = new AgentOrchestratorKernel();
        var terminalIds = new HashSet<string>(StringComparer.Ordinal);
        var dependencyIds = new List<string>();
        for (var index = 0; index < 200; index++)
        {
            var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, $"Terminal {index}");
            terminalIds.Add(goal.Id.Value);
            if (index < 2)
                dependencyIds.Add(goal.Id.Value);
            GoalOperationJournal.Completed(workspace.ExecutionDirectory, goal, "test:terminal", "Terminal load fixture.");
        }
        var openIds = Enumerable.Range(0, 3).Select(index => GoalLifecycleCommands.CreateAndActivateSimpleGoal(
            kernel, AgentCatalog.Default().Agents, $"Open {index}").Id.Value).ToArray();
        for (var index = 0; index < dependencyIds.Count; index++)
            kernel.SetGoalDependency(new GoalId(openIds[index]), new GoalId(dependencyIds[index]));
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => terminalIds.Contains(goal.Id) ? goal with { Status = GoalStatus.Completed } : goal).ToArray()
        });
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        repository.SaveAsync(kernel).GetAwaiter().GetResult();
        Assert.Equal(203, repository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult().Count);
        return (repository, openIds, dependencyIds.ToArray());
    }
}
