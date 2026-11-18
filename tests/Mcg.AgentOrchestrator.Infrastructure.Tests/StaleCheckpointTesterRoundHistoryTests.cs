using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static ConductorDriverTests;

[Collection("IsolatedProcessSpawningRemainderC")]
[Xunit.Trait("Isolation", "ProcessSpawning")]
public sealed class StaleCheckpointTesterRoundHistoryTests(WorkerBuildScopeDispatchFixtureTemplate template)
    : IClassFixture<WorkerBuildScopeDispatchFixtureTemplate>
{
    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "CrossTick")]
    public async Task ReadyTester_RejectedStartPreservesPredecessorAndNextRound(bool completeDeveloperInTick)
    {
        using var fixture = template.Copy();
        using var harness = new Harness(fixture, completeDeveloperInTick);
        var developer = fixture.Predecessor;
        var receipt = developer.LastDispatch!.WorkerBuildScope;
        var developerHistory = developer.ToSnapshot().VerificationHistory!.ToArray();

        harness.RejectNextStart = true;
        Assert.IsType<ConductorAdvanceOutcome.Held>(harness.Advance().Outcome);
        harness.AssertAbortedStart(0);
        harness.AssertDeveloperUnchanged(developerHistory, receipt);

        // A refresh on the next tick must not handle the predecessor again.
        harness.Refresh();
        harness.AssertDeveloperUnchanged(developerHistory, receipt);
        harness.AdvanceExpectingExecution();
        harness.AssertStartedScope();
        Assert.Null(fixture.Kernel.GetSnapshotBeforeDispatchStart(fixture.GoalId));
        var process = Assert.IsType<TaskProcessRecord>(fixture.Successor.LastProcess);
        await TestOwnedDispatchProcesses.AwaitCompletionAsync(process);
        Assert.True(File.Exists(process.ExitCodePath), "Tester host exited without its exit artifact.");
        harness.Refresh();
        harness.AssertTesterHistory(1, stored: false);

        // Refresh and repair dispatch share a tick in production. Reject that repair
        // after publication too: its already-recorded completion must remain durable.
        harness.RejectNextStart = true;
        Assert.IsType<ConductorAdvanceOutcome.Held>(harness.Advance().Outcome);
        harness.AssertAbortedStart(1);
        harness.Refresh();
        harness.AssertTesterHistory(1);
        harness.AssertDeveloperUnchanged(developerHistory, receipt);
        harness.AssertLogMatchesStore();
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public void CompletedTester_RejectedRepairDoesNotReplayCompletionOnNextTick()
    {
        using var fixture = template.Copy();
        using var harness = new Harness(fixture, completeDeveloperInTick: false);
        var developerHistory = fixture.Predecessor.ToSnapshot().VerificationHistory!.ToArray();
        var receipt = fixture.Predecessor.LastDispatch!.WorkerBuildScope;

        // Persist an exited round before the tick begins, then refresh it in the
        // tick. This distinguishes dropped tick progress from duplicate recording.
        fixture.Prepare(fixture.Successor);
        fixture.CompleteProcess(fixture.Successor);
        File.WriteAllText(fixture.Successor.LastProcess!.StandardOutputPath, "Tester exited without a terminal contract.");
        harness.SaveTick();
        harness.Refresh();
        harness.AssertTesterHistory(1, stored: false);
        harness.RejectNextStart = true;

        Assert.IsType<ConductorAdvanceOutcome.Held>(harness.Advance().Outcome);
        harness.AssertAbortedStart(1);
        harness.AssertDeveloperUnchanged(developerHistory, receipt);

        harness.Refresh();
        harness.AssertTesterHistory(1);
        harness.AdvanceExpectingExecution();
        Assert.True(harness.DurableBaselineUpdates > 0, "Tester repair never reported its durable retry-admission baseline.");
        harness.AssertStartedScope();
        harness.SaveTick();
        harness.AssertTesterHistory(1);
        harness.AssertDeveloperUnchanged(developerHistory, receipt);
        harness.AssertLogMatchesStore();
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "CrossTick")]
    public void CommittedStart_RejectedRunnerKeepsDispatchAndReservation(bool repair)
    {
        using var fixture = template.Copy();
        using var harness = new Harness(fixture, completeDeveloperInTick: false);
        var developerHistory = fixture.Predecessor.ToSnapshot().VerificationHistory!.ToArray();
        var receipt = fixture.Predecessor.LastDispatch!.WorkerBuildScope;
        if (repair)
        {
            fixture.Prepare(fixture.Successor);
            fixture.CompleteProcess(fixture.Successor);
            File.WriteAllText(fixture.Successor.LastProcess!.StandardOutputPath, "Tester exited without a terminal contract.");
            harness.SaveTick();
            harness.Refresh();
        }
        var completedRounds = repair ? 1 : 0;
        // A repair also commits BeforeRetryAdmission and reserves paid admission.
        // Reject only the runner checkpoint, after it captures the scope identity.
        harness.RejectAtCheckpoint = repair ? 3 : 2;
        harness.RejectNextStart = true;
        Assert.IsType<ConductorAdvanceOutcome.Held>(harness.Advance().Outcome);
        var committedAt = Assert.IsType<TaskDispatchRecord>(harness.LastCommittedDispatch).DispatchedAt;
        harness.AssertCommittedStart(committedAt, completedRounds);
        harness.AssertTesterHistory(completedRounds);
        harness.AssertDeveloperUnchanged(developerHistory, receipt);
        if (repair)
            Assert.True(harness.DurableBaselineUpdates > 0, "Repair did not persist its retry-admission reservation.");

        harness.Refresh();
        harness.SaveTick();
        harness.AssertCommittedStart(committedAt, completedRounds);
        harness.AssertTesterHistory(completedRounds);
        harness.AssertDeveloperUnchanged(developerHistory, receipt);
        harness.AssertLogMatchesStore();
    }

    private sealed class Harness : IDisposable
    {
        private readonly WorkerBuildScopeDispatchFixture fixture;
        private readonly MutableClock clock = new();
        private readonly SqliteOrchestratorStateRepository repository;
        private readonly GoalLifecycleEventWriter writer;
        private readonly Dictionary<string, GoalSnapshot> baselines;
        private readonly TestOwnedDispatchProcesses processes;
        private readonly ConductorDriver driver;
        private int conflicts;
        private int publicationChecks;
        private int operatorUpdates;
        private int checkpointCalls;
        private readonly StateLogEntry[] developerEvents;
        internal bool RejectNextStart { get; set; }
        internal int RejectAtCheckpoint { get; set; } = 1;
        internal TaskDispatchRecord? LastCommittedDispatch { get; private set; }
        internal int DurableBaselineUpdates { get; private set; }
        private AgentOrchestratorKernel Kernel => fixture.Kernel;
        private Goal Goal => Kernel.GetGoal(fixture.GoalId);
        private const string OperatorNote = "operator update after Tester scope publication";

        internal Harness(WorkerBuildScopeDispatchFixture fixture, bool completeDeveloperInTick)
        {
            this.fixture = fixture;
            // The seeded Developer bypasses DispatchProcessHost, which installs these
            // exclusions on a real start. Keep Tester's generated handoff out of the
            // candidate tree so it cannot invalidate the predecessor's build receipt.
            Assert.True(OrchestratorWorktreeGitExclude.Apply(fixture.Root));
            fixture.Kernel = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot(), clock);
            Kernel.SetGoalRefinedSpec(fixture.GoalId, new RefinedSpec(
                "Preserve Tester round history", ["Record each completed round once."],
                VerificationClass.TestVerifiable, [], []));
            Kernel.ReassignTaskAgent(fixture.GoalId, fixture.SuccessorId,
                DefaultAgents().First(agent => agent.Role == AgentRole.Tester));
            if (!completeDeveloperInTick)
                fixture.Reconcile();
            var workspace = OrchestratorWorkspace.ForDirectory(fixture.Main);
            repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
            writer = new GoalLifecycleEventWriter(workspace.GoalLifecycleEventsDirectory);
            foreach (var entry in Goal.Timeline)
                writer.AppendTimelineEvent(entry);
            Kernel.SetEventWriter(writer);
            repository.SaveAsync(Kernel).GetAwaiter().GetResult();
            baselines = Kernel.ExportSnapshot().Goals.ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
            if (completeDeveloperInTick)
                fixture.Reconcile();
            Assert.Equal(WorkTaskStatus.Completed, fixture.Predecessor.Status);
            var initialReceipt = fixture.Receipt();
            Assert.True(initialReceipt.Matches, $"Developer setup receipt: {initialReceipt.Reason}: {initialReceipt.Detail}");
            developerEvents = Goal.Timeline.Select(StateLogEntry.FromProgressEvent)
                .Where(IsDeveloperCompletionEvent).OrderBy(entry => entry.OccurredAtUtcTicks).ToArray();
            processes = new TestOwnedDispatchProcesses(Kernel, () => Goal);
            driver = new ConductorDriver(Kernel, workspace, new FakeAcceptanceVerifier(), DefaultAgents(),
                WorkerProfileCatalog.Default(), persistCriticalDispatchStart: (checkpoint, ids) =>
                {
                    checkpointCalls++;
                    if (RejectNextStart && checkpointCalls == RejectAtCheckpoint)
                    {
                        RejectNextStart = false;
                        var published = JsonSerializer.Deserialize<WorkerBuildProjectScope.Metadata>(
                            File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(fixture.Root)))!;
                        Assert.Equal(fixture.GoalId.Value, published.GoalId);
                        Assert.Equal(fixture.SuccessorId.Value, published.TaskId);
                        // Publication precedes both the initial-start checkpoint and
                        // the repair's BeforeRetryAdmission checkpoint. The runner
                        // captures the dispatch scope identity only after admission.
                        var dispatch = Assert.IsType<TaskDispatchRecord>(
                            checkpoint.GetTask(fixture.GoalId, fixture.SuccessorId).LastDispatch);
                        if (RejectAtCheckpoint == 1)
                            Assert.Null(dispatch.WorkerBuildScope);
                        else
                            Assert.NotNull(dispatch.WorkerBuildScope);
                        publicationChecks++;
                        var concurrent = repository.LoadAsync().GetAwaiter().GetResult();
                        concurrent.SetEventWriter(writer);
                        concurrent.RecordTaskNote(fixture.GoalId, fixture.SuccessorId, $"{OperatorNote} {++operatorUpdates}");
                        repository.SaveAsync(concurrent).GetAwaiter().GetResult();
                    }
                    try
                    {
                        CliPersistentStateRunner.PersistCriticalGoalSnapshotsOrThrow(repository, checkpoint, ids,
                            baselines, workspace.SqliteStatePath, results =>
                            {
                                foreach (var result in results)
                                {
                                    CliPersistentStateRunner.RebaseCheckpointAfterDurableSave(checkpoint, result);
                                    if (result.PersistedSnapshot is { } persisted)
                                        baselines[result.GoalId] = persisted;
                                }
                            });
                        LastCommittedDispatch = checkpoint.GetTask(fixture.GoalId, fixture.SuccessorId).LastDispatch;
                    }
                    catch (DispatchCheckpointConflictException ex)
                    {
                        Assert.Contains("rejected stale state", ex.Message, StringComparison.Ordinal);
                        conflicts++;
                        throw;
                    }
                }, recordDurableGoalBaseline: snapshot =>
                {
                    Assert.Equal(fixture.GoalId.Value, snapshot.Id);
                    // Retry admission and start claims save outside this callback's
                    // critical checkpoints, just as they do in the production loop.
                    baselines[snapshot.Id] = snapshot;
                    DurableBaselineUpdates++;
                });
        }

        internal ConductorAdvanceResult Advance()
        {
            checkpointCalls = 0;
            clock.Advance();
            return driver.AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);
        }

        internal void AdvanceExpectingExecution()
        {
            var result = Advance();
            Assert.True(result.Outcome is ConductorAdvanceOutcome.Executed,
                $"Expected Tester execution; actual={result.Outcome}; " +
                $"conflicts={conflicts}; injectedConflicts={publicationChecks}; durableBaselineUpdates={DurableBaselineUpdates}");
            Assert.Equal(publicationChecks, conflicts);
        }

        internal void Refresh()
        {
            clock.Advance();
            new GoalDispatchOperations().RefreshDispatches(Kernel, Goal,
                new BackgroundDispatchRunner(clock, isStillRunning: _ => false, findBuildDaemons: _ => [],
                    resolveWorkerBuildReceiptPath: _ => fixture.ReceiptPath,
                    runOrchestratorBuildCheck: _ => throw new InvalidOperationException(
                        "Incomplete Tester output must not launch a completion build.")));
        }

        internal void SaveTick()
        {
            repository.SaveAsync(Kernel).GetAwaiter().GetResult();
            baselines[fixture.GoalId.Value] = Kernel.ExportGoalSnapshot(fixture.GoalId);
        }

        private Goal Stored => repository.LoadAsync().GetAwaiter().GetResult().GetGoal(fixture.GoalId);

        internal void AssertStartedScope()
        {
            var scope = Assert.IsType<WorkerBuildScopeIdentity>(fixture.Successor.LastDispatch!.WorkerBuildScope);
            Assert.Equal(scope.MetadataDigest, WorkerBuildProjectScope.ReadDispatchIdentity(fixture.Root).MetadataDigest);
        }

        internal void AssertCommittedStart(DateTimeOffset committedAt, int completedRounds)
        {
            Assert.Equal(1, conflicts);
            Assert.Equal(1, publicationChecks);
            Assert.Null(Kernel.GetSnapshotBeforeDispatchStart(fixture.GoalId));
            foreach (var goal in new[] { Goal, Stored })
            {
                var tester = goal.Tasks.Single(task => task.Id == fixture.SuccessorId);
                Assert.Equal(committedAt, tester.LastDispatch!.DispatchedAt);
                Assert.Equal(completedRounds + 1, tester.DispatchHistory.Count);
                Assert.Equal(completedRounds, goal.Timeline.Count(entry =>
                    entry.TaskId == tester.Id && entry.Kind == ProgressKind.TaskProcessStarted));
                Assert.Equal(operatorUpdates, goal.Timeline.Count(entry =>
                    entry.Message.StartsWith(OperatorNote, StringComparison.Ordinal)));
                Assert.Null(tester.LastProcess);
                if (completedRounds > 0)
                {
                    var admission = Assert.Single(tester.RetryAdmissionHistory.Where(record => record.LinkedDispatchAt == committedAt));
                    Assert.NotNull(admission.ReservationOwnerId);
                    Assert.Null(admission.WorkerStartClaimedAt);
                }
                Assert.Equal(completedRounds + 1, goal.Timeline.Count(entry => entry.TaskId == tester.Id &&
                    entry.Kind == ProgressKind.TaskDispatchRecorded));
            }
            Assert.Equal(completedRounds + 1, Log.Count(line => line.Entry.TaskId == fixture.SuccessorId.Value &&
                line.Entry.Kind == nameof(ProgressKind.TaskDispatchRecorded)));
        }

        internal void AssertAbortedStart(int completedRounds)
        {
            Assert.Equal(publicationChecks, conflicts);
            Assert.True(conflicts > 0);
            foreach (var goal in new[] { Goal, Stored })
            {
                var tester = goal.Tasks.Single(task => task.Id == fixture.SuccessorId);
                if (completedRounds == 0)
                {
                    Assert.Null(tester.LastDispatch);
                    Assert.Null(tester.LastProcess);
                }
                else
                {
                    // A rejected repair must retain the settled earlier attempt,
                    // without giving the aborted attempt a dispatch or process.
                    Assert.Equal(tester.DispatchHistory.Single().DispatchedAt, tester.LastDispatch!.DispatchedAt);
                    Assert.NotNull(tester.LastProcess);
                    Assert.False(tester.LastProcess.IsRunning);
                }
                Assert.Equal(completedRounds, tester.DispatchHistory.Count);
                Assert.Equal(completedRounds, goal.Timeline.Count(entry =>
                    entry.TaskId == tester.Id && entry.Kind == ProgressKind.TaskProcessStarted));
                Assert.Equal(operatorUpdates, goal.Timeline.Count(entry =>
                    entry.Message.StartsWith(OperatorNote, StringComparison.Ordinal)));
            }
            // Surviving publication belongs to the rejected successor. Its retained
            // predecessor scope must remain authoritative for receipt validation.
            Assert.Equal(fixture.SuccessorId.Value, JsonSerializer.Deserialize<WorkerBuildProjectScope.Metadata>(
                File.ReadAllBytes(WorkerBuildProjectScope.MetadataPath(fixture.Root)))!.TaskId);
            AssertTesterHistory(completedRounds);
            Assert.Null(Kernel.GetSnapshotBeforeDispatchStart(fixture.GoalId));
        }

        internal void AssertTesterHistory(int expected, bool stored = true)
        {
            foreach (var goal in stored ? new[] { Goal, Stored } : new[] { Goal })
            {
                var tester = goal.Tasks.Single(task => task.Id == fixture.SuccessorId);
                Assert.Equal(expected, tester.VerificationHistory.Count);
                Assert.Equal(expected, goal.Timeline.Count(entry => entry.TaskId == tester.Id &&
                    entry.Kind == ProgressKind.TaskVerificationRecorded));
                Assert.Equal(expected, goal.Timeline.Count(entry => entry.TaskId == tester.Id &&
                    entry.Kind == ProgressKind.TaskRetried && IsRepair(entry.Message)));
                foreach (var verification in tester.VerificationHistory)
                    Assert.Equal(TaskOutcomeRules.VerificationTerminalContractIncomplete.Token, verification.CompletionVerdictRule);
            }
            Assert.Equal(expected, Log.Count(line => line.Entry.TaskId == fixture.SuccessorId.Value &&
                line.Entry.Kind == nameof(ProgressKind.TaskVerificationRecorded)));
            Assert.Equal(expected, Log.Count(line => line.Entry.TaskId == fixture.SuccessorId.Value &&
                line.Entry.Kind == nameof(ProgressKind.TaskRetried) && IsRepair(line.Entry.Message)));
            if (expected == 1)
                Assert.Contains(Goal.Timeline, entry => entry.TaskId == fixture.SuccessorId &&
                    entry.Kind == ProgressKind.TaskRetried && entry.Message.StartsWith("Tester terminal contract repair 1/2", StringComparison.Ordinal));
        }

        internal void AssertDeveloperUnchanged(TaskVerificationSnapshot[] history, WorkerBuildScopeIdentity? scope)
        {
            foreach (var goal in new[] { Goal, Stored })
            {
                var developer = goal.Tasks.Single(task => task.Id == fixture.PredecessorId);
                Assert.Equal(WorkTaskStatus.Completed, developer.Status);
                // Compare the durable contract on both sides: live-only fields such as
                // HasCommittedChanges are intentionally absent after a SQLite reload.
                Assert.Equal(JsonSerializer.Serialize(history), JsonSerializer.Serialize(developer.ToSnapshot().VerificationHistory));
                Assert.Equal(scope, developer.LastDispatch!.WorkerBuildScope);
                Assert.Equal(developerEvents, goal.Timeline.Select(StateLogEntry.FromProgressEvent)
                    .Where(IsDeveloperCompletionEvent).OrderBy(entry => entry.OccurredAtUtcTicks).ToArray());
                Assert.DoesNotContain(developer.VerificationHistory, verification =>
                    verification.StandardError.Contains("worker_build_scope=invalid", StringComparison.Ordinal));
            }
            Assert.Equal(developerEvents, Log.Select(line => line.Entry)
                .Where(IsDeveloperCompletionEvent).OrderBy(entry => entry.OccurredAtUtcTicks).ToArray());
            var receipt = fixture.Receipt();
            Assert.True(receipt.Matches, $"{receipt.Reason}: {receipt.Detail}");
        }

        private bool IsDeveloperCompletionEvent(StateLogEntry entry) =>
            entry.TaskId == fixture.PredecessorId.Value &&
            // Tester admission adds these audit notes even when the Developer's
            // completion is untouched. All other events, including unexpected
            // notes, must exactly match the baseline in memory, SQLite and the log.
            !(entry.Kind == nameof(ProgressKind.TaskNote) && entry.Message is
                "developer-completion structural pre-check passed: source size ratchet and acceptance manifest lane consistency checks passed" or
                "pre-tester-deferred-evidence skipped: exit=no-resolved-classes prior=<none> parsed=<none> not_run=<none>");

        private StateLogLine[] Log => File.ReadLines(writer.EventFilePath(fixture.GoalId))
            .Select(StateLogDivergenceComparer.ParseLine).OfType<StateLogLine>().ToArray();

        internal void AssertLogMatchesStore()
        {
            var report = StateLogDivergenceComparer.Compare(Stored.Timeline.Select(StateLogEntry.FromProgressEvent), Log);
            Assert.Equal(0, report.Repeated);
            Assert.Equal(0, report.Lost);
            Assert.Equal(0, report.StoredOnly);
        }

        private static bool IsRepair(string message) => message.StartsWith("Tester terminal contract repair", StringComparison.Ordinal);
        public void Dispose() => processes.Dispose();
    }

    private sealed class MutableClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = DateTimeOffset.Parse("2026-10-20T12:00:00Z");
        internal void Advance() => UtcNow = UtcNow.AddSeconds(1);
    }
}
