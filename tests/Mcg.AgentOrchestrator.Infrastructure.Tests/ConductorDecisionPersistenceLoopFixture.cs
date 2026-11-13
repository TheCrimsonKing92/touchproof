using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Each fixture owns its database, git repository and event log; no workers are launched.
internal sealed class ConductorDecisionPersistenceLoopFixture : ConductorBatchLoopTests, IDisposable
{
    internal const string ReadmissionMessage = "Batch loop re-admitted escalated goal after state changed";
    internal const string TerminalizationMessage = "Goal terminalized from merge evidence at";
    private readonly string _root = CreateTempDirectory("mcg-decision-persistence");
    private readonly Dictionary<string, GoalSnapshot> _baselines = new(StringComparer.Ordinal);
    private readonly SqliteOrchestratorStateRepository _repository;
    private readonly OrchestratorWorkspace _workspace;
    private readonly GoalLifecycleEventWriter _writer;
    private int _sweepTick;
    private bool _tickOpen;
    private bool _skipTerminalCheckpoint;
    internal AgentOrchestratorKernel Kernel { get; private set; } = new();
    internal GoalId GoalId { get; private set; } = null!;
    internal List<(int Tick, GoalSnapshot Snapshot)> StoredCheckpoints { get; } = [];
    internal List<GoalSnapshotSaveDisposition> SaveDispositions { get; } = [];
    internal int CriticalAttempts { get; private set; }
    internal int CriticalConflicts { get; private set; }
    internal int DriverCalls { get; private set; }
    internal bool TerminalizedListObserved { get; private set; }
    internal bool SweepFaultObserved { get; private set; }

    internal ConductorDecisionPersistenceLoopFixture(ITestOutputHelper output) : base(output)
    {
        _workspace = OrchestratorWorkspace.ForDirectory(_root);
        _repository = OpenStateRepository(_workspace.SqliteStatePath);
        _writer = new GoalLifecycleEventWriter(_workspace.GoalLifecycleEventsDirectory);
    }

    private void Seed(bool verified)
    {
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(), "Persist policy decisions");
        GoalId = goal.Id;
        var task = goal.Tasks.Single();
        if (verified)
        {
            PassVerification(Kernel, goal, task);
            Assert.Equal(GoalStatus.Verified, goal.Status);
        }
        else
        {
            Kernel.ReportTaskProgress(goal.Id, task.Id, WorkTaskStatus.Failed, "fixture seed");
        }
        foreach (var entry in goal.Timeline) _writer.AppendTimelineEvent(entry);
        Kernel.SetEventWriter(_writer);
        _repository.SaveAsync(Kernel).GetAwaiter().GetResult();
        foreach (var snapshot in Kernel.ExportSnapshot().Goals) _baselines[snapshot.Id] = snapshot;
    }

    private TerminalGoalSweepResult Reload()
    {
        _sweepTick++;
        _tickOpen = true;
        Kernel.RefreshTrackedGoals(_repository.LoadAsync().GetAwaiter().GetResult().ExportSnapshot());
        return new TerminalGoalSweepResult([]) { ReloadBaseline = GoalKernelChange.CaptureAll(Kernel) };
    }

    internal Goal StoredGoal() => _repository.LoadAsync().GetAwaiter().GetResult().GetGoal(GoalId);

    internal IReadOnlyList<StateLogLine> ReadLog() => File.ReadLines(_writer.EventFilePath(GoalId))
        .Select(StateLogDivergenceComparer.ParseLine).OfType<StateLogLine>().ToArray();

    private void Apply(AgentOrchestratorKernel kernel, IReadOnlyList<GoalSnapshotSaveResult> results)
    {
        foreach (var result in results)
        {
            SaveDispositions.Add(result.Disposition);
            CliPersistentStateRunner.RebaseCheckpointAfterDurableSave(kernel, result);
            if (result.PersistedSnapshot is not { } snapshot) continue;
            _baselines[result.GoalId] = snapshot;
            if (_tickOpen) StoredCheckpoints.Add((_sweepTick, snapshot));
        }
    }

    private IReadOnlyList<GoalSnapshotCheckpointResult> Checkpoint(
        AgentOrchestratorKernel kernel, IReadOnlyCollection<GoalId> ids)
    {
        var requests = CliPersistentStateRunner.BuildConductLoopCheckpointRequests(
            kernel, ids, _baselines, _repository, _workspace.SqliteStatePath, true, out var holds);
        if (_skipTerminalCheckpoint && requests.Any(request => request.Current.Status == GoalStatus.Completed))
        {
            _skipTerminalCheckpoint = false;
            var external = _repository.LoadAsync().GetAwaiter().GetResult();
            external.SetEventWriter(_writer);
            external.RecordGoalPolicyDecision(GoalId, "fixture store advanced before terminal checkpoint");
            _repository.SaveAsync(external).GetAwaiter().GetResult();
            // Force a durable Skipped result at this seam, then apply the production rebase.
            // RejectConflict is controlled here; normal sweep checkpoints do not request it.
            requests = requests.Select(request => request with { RejectConflict = true }).ToArray();
        }
        var outcomes = _repository.CheckpointGoalSnapshotsAsync(requests).GetAwaiter().GetResult();
        Apply(kernel, outcomes.Where(outcome => outcome.IsDurable && outcome.SaveResult is not null)
            .Select(outcome => outcome.SaveResult!).ToArray());
        kernel.EvictTerminalGoalAggregates(outcomes.Where(outcome => outcome.IsDurable &&
            outcome.SaveResult?.PersistedSnapshot?.Status == GoalStatus.Completed)
            .Select(outcome => new GoalId(outcome.GoalId)).ToArray());
        return holds.Concat(outcomes).ToArray();
    }

    internal void RunReadmission(string scenario)
    {
        Seed(verified: false);
        var taskId = Kernel.GetGoal(GoalId).Tasks.Single().Id;
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ =>
            {
                DriverCalls++;
                if (scenario == "conflict")
                {
                    CriticalAttempts++;
                    try
                    {
                        CliPersistentStateRunner.PersistCriticalGoalSnapshotsOrThrow(
                            _repository, Kernel, [GoalId], _baselines, _workspace.SqliteStatePath,
                            results => Apply(Kernel, results));
                    }
                    catch (DispatchCheckpointConflictException)
                    {
                        CriticalConflicts++;
                    }
                }
                return DispatchStartOutcome.Started();
            });
        new ConductorBatchLoop(measuredSweep: _ => Reload()).Run(
            Kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(_root, "stop"),
            maxIterations: 2, watchInterval: TimeSpan.FromSeconds(1), sleepFunc: _ => false,
            onTick: tick =>
            {
                _tickOpen = false;
                if (tick.Tick != 1) return;
                var external = _repository.LoadAsync().GetAwaiter().GetResult();
                external.SetEventWriter(_writer);
                external.RetryTask(GoalId, taskId, "fixture operator repaired task");
                if (scenario == "idle") external.ParkGoal(GoalId, "fixture parked repaired goal");
                _repository.SaveAsync(external).GetAwaiter().GetResult();
            }, checkpointGoalTick: Checkpoint);
        // A separate tick reload must not erase the first tick's decision.
        _tickOpen = false;
        Kernel.RefreshTrackedGoals(_repository.LoadAsync().GetAwaiter().GetResult().ExportSnapshot());
    }

    internal void RunMergeEvidence(string scenario)
    {
        BatchLoopGitRepositoryTemplate.CopyTo(_root);
        Seed(verified: true);
        _skipTerminalCheckpoint = scenario == "skipped";
        var resolver = new FixedEvidence(GoalId);
        var loop = new ConductorBatchLoop(measuredSweep: current =>
        {
            var reload = Reload();
            var result = TerminalGoalSweep.Run(current, _root, TrunkBranchName.Default, GoalId,
                integrationEvidenceResolver: resolver,
                attentionStore: CollaborationItemStore.ForDirectory(_workspace.OrchestratorDirectory),
                cleanupHooks: new GoalWorktreeCleanupHooks(), orchestratorDirectory: _workspace.OrchestratorDirectory);
            TerminalizedListObserved |= result.ExplicitlyTerminalizedGoalIds.Contains(GoalId);
            if (_sweepTick == 1 && scenario == "fault")
            {
                Assert.Contains(current.GetGoal(GoalId).Timeline,
                    entry => entry.Message.StartsWith(TerminalizationMessage, StringComparison.Ordinal));
                SweepFaultObserved = true;
                throw new InvalidOperationException("fixture failure after sweep terminalization");
            }
            return result with { ReloadBaseline = reload.ReloadBaseline };
        });
        var driver = MakeDriver(createWorkspace: _ =>
        {
            DriverCalls++;
            throw new InvalidOperationException("terminalized goal entered ordinary walk");
        });
        for (var tick = 0; tick < 2; tick++)
        {
            loop.Run(Kernel, driver, ConductorAutonomyPolicy.Conservative, Path.Combine(_root, "stop"),
                maxIterations: 1, checkpointGoalTick: Checkpoint);
            _tickOpen = false;
        }
    }

    internal ProgressEvent AssertStoredDecision(string message, int expectedTick)
    {
        var stored = Assert.Single(StoredGoal().Timeline.Where(entry =>
            entry.Kind == ProgressKind.GoalPolicyDecision && entry.Message.StartsWith(message, StringComparison.Ordinal)));
        Assert.Contains(StoredCheckpoints, checkpoint => checkpoint.Tick == expectedTick &&
            checkpoint.Snapshot.Timeline.Any(entry => entry.Kind == stored.Kind &&
                entry.OccurredAt == stored.OccurredAt && entry.Message == stored.Message));
        var logged = Assert.Single(ReadLog().Where(line => line.Entry.Message.StartsWith(message, StringComparison.Ordinal)));
        Assert.Equal(StateLogEntry.FromProgressEvent(stored), logged.Entry);
        return stored;
    }

    public void Dispose() => TryDeleteDirectory(_root);

    private sealed class FixedEvidence(GoalId goalId) : IGoalIntegrationEvidenceResolver
    {
        public bool TryResolve(GoalId requested, out GoalIntegrationEvidence? evidence)
        {
            evidence = requested == goalId ? new GoalIntegrationEvidence("fixture-integrate", "fixture-main", "fixture merge") : null;
            return evidence is not null;
        }
    }
}
