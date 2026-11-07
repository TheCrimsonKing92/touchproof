using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AttributedFailureDeveloperRouteTests;

// Parallel-safe: private SQLite stores and kernels, injected clock and worker dispatch.
public sealed class AttributedFailureRoutingHoldTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task CohortRoute_InHoldNote_DeliversBeforeAutomaticFactsAtDeadline()
    {
        using var scenario = new CohortRoute();
        var failureAt = scenario.Now;
        scenario.RetryElapsed = TimeSpan.FromSeconds(1);
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        Assert.Contains("waiting-for-operator-routing", held.Reason);
        var hold = Assert.IsType<AcceptanceRetryRoutingHold>(scenario.Task.AcceptanceRetryRoutingHold);
        Assert.Equal(failureAt, hold.FailureRecordedAt);
        Assert.Equal(180, scenario.Policy.AutomaticRetryOperatorRoutingHoldSeconds);
        Assert.Equal(failureAt.AddSeconds(scenario.Policy.AutomaticRetryOperatorRoutingHoldSeconds), hold.Deadline);
        Assert.Equal(scenario.Task.LatestRetryAt, hold.RoundRetryAt);
        Assert.Null(hold.AttemptId);
        var feedback = Assert.Single(MarkerRetries(scenario.Goal)).Message;
        Assert.StartsWith("ATTRIBUTED_FAILURE_ROUTE source=cohort", feedback);
        Assert.Equal(feedback, Assert.Single(hold.AutomaticFeedback));
        Assert.Equal(WorkTaskStatus.Assigned, scenario.Task.Status);
        Assert.Empty(scenario.DeliveredFeedback);
        Assert.Contains(scenario.Goal.Timeline, entry =>
            entry.Message.Contains("waiting-for-operator-routing", StringComparison.Ordinal));

        scenario.Now = failureAt.AddSeconds(60);
        const string instruction = "Preserve the attributed cohort failure evidence.";
        var intent = await scenario.Route(instruction);
        Assert.Equal(OperatorIntentStatus.Applied, intent.Status);
        Assert.False(OperatorRoutingNote.IsQueuedOutcome(intent.Outcome));
        scenario.Now = hold.Deadline.AddTicks(-1);
        Assert.Contains("waiting-for-operator-routing",
            Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome).Reason);
        Assert.Empty(scenario.DeliveredFeedback);
        Assert.Equal(hold, scenario.Task.AcceptanceRetryRoutingHold);
        Assert.Single(MarkerRetries(scenario.Goal));

        scenario.Now = hold.Deadline;
        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        var block = Assert.Single(Assert.Single(scenario.DeliveredFeedback));
        Assert.StartsWith("Later operator notes override earlier ones where they conflict.", block);
        Assert.Contains(instruction, block);
        Assert.Contains(feedback, block);
        Assert.True(block.IndexOf(instruction, StringComparison.Ordinal) <
            block.IndexOf("Automatic failure facts (not instructions):", StringComparison.Ordinal));
        Assert.True(block.IndexOf("Automatic failure facts (not instructions):", StringComparison.Ordinal) <
            block.IndexOf(feedback, StringComparison.Ordinal));
        Assert.Equal(scenario.Now, Assert.Single(scenario.Task.OperatorRoutingNotes).DeliveredAt);
        Assert.Equal(hold.RoundRetryAt, Assert.Single(scenario.Task.OperatorRoutingNotes).TargetRoundRetryAt);
        Assert.Equal(block, scenario.Task.AcceptedRetryFeedback?.Message);
        Assert.Equal(WorkTaskStatus.Running, scenario.Task.Status);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task CohortRoute_AfterHoldNote_IsAcceptedAndQueuedWhileRunning()
    {
        using var scenario = new CohortRoute();
        Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Advance().Outcome);
        var hold = scenario.Task.AcceptanceRetryRoutingHold!;
        var original = scenario.Task.CriterionRetryFeedback.ToArray();
        scenario.Now = hold.Deadline;
        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);
        Assert.Equal(WorkTaskStatus.Running, scenario.Task.Status);
        Assert.Equal(original, Assert.Single(scenario.DeliveredFeedback));
        var retryAt = scenario.Task.LatestRetryAt;
        var dispatch = scenario.Task.LastDispatch;
        scenario.Now = hold.Deadline.AddSeconds(1);

        var intent = await scenario.Route("Keep this diagnosis for the next round.");

        Assert.Equal(OperatorIntentStatus.Applied, intent.Status);
        Assert.True(OperatorRoutingNote.IsQueuedOutcome(intent.Outcome));
        Assert.DoesNotContain("task-not-retryable", intent.Outcome ?? "");
        var note = Assert.Single(scenario.Task.OperatorRoutingNotes);
        Assert.True(note.QueuedWhileRunning);
        Assert.Null(note.DeliveredAt);
        Assert.Null(note.TargetRoundRetryAt);
        Assert.Equal(WorkTaskStatus.Running, scenario.Task.Status);
        Assert.Equal(retryAt, scenario.Task.LatestRetryAt);
        Assert.Equal(dispatch, scenario.Task.LastDispatch);
        Assert.Equal(original, scenario.Task.CriterionRetryFeedback);
        Assert.Single(scenario.DeliveredFeedback);
    }

    [Fact]
    public void CohortRoute_ZeroHold_DispatchesImmediately()
    {
        using var scenario = new CohortRoute(holdSeconds: 0);
        var failureAt = scenario.Now;

        Assert.IsType<ConductorAdvanceOutcome.Executed>(scenario.Advance().Outcome);

        var hold = Assert.IsType<AcceptanceRetryRoutingHold>(scenario.Task.AcceptanceRetryRoutingHold);
        Assert.Equal(failureAt, hold.FailureRecordedAt);
        Assert.Equal(failureAt, hold.Deadline);
        Assert.Equal(failureAt, scenario.Now);
        Assert.Equal(WorkTaskStatus.Running, scenario.Task.Status);
        Assert.Single(scenario.DeliveredFeedback);
        Assert.Null(scenario.Task.AcceptedRetryFeedback);
        Assert.DoesNotContain(scenario.Goal.Timeline, entry =>
            entry.Message.Contains("waiting-for-operator-routing", StringComparison.Ordinal));
    }

    private sealed class CohortRoute : IDisposable
    {
        private const long StateVersion = 7;
        private readonly string _root = ConductorDriverTests.CreateTempDirectory();
        private readonly AgentOrchestratorKernel _kernel;
        private readonly SqliteOperatorIntentStore _store;
        private readonly OperatorIntentCoordinator _coordinator;
        private readonly ConductorDriver _driver;
        internal Goal Goal { get; }
        internal TaskSpec Task => Goal.Tasks.Single();
        internal DateTimeOffset Now { get; set; }
        internal TimeSpan RetryElapsed { get; set; }
        internal ConductorAutonomyPolicy Policy { get; }
        internal List<string[]> DeliveredFeedback { get; } = [];

        internal CohortRoute(int? holdSeconds = null)
        {
            // Reuse the existing cohort producer and the routing scenario's intent/store setup.
            var (kernel, first, second) = CohortGoals();
            _kernel = kernel;
            Goal = second;
            Assert.True(ApplyCohort(kernel, first, second).ShouldRecord);
            Now = DateTimeOffset.UtcNow;
            Policy = holdSeconds is { } seconds
                ? ConductorAutonomyPolicy.Permissive with { AutomaticRetryOperatorRoutingHoldSeconds = seconds }
                : ConductorAutonomyPolicy.Permissive;
            var workspace = OrchestratorWorkspace.ForDirectory(_root);
            _store = new SqliteOperatorIntentStore(Path.Combine(workspace.OrchestratorDirectory,
                SqliteOperatorIntentStore.DatabaseFileName), workspace.LogDirectory, utcNow: () => Now);
            _coordinator = new OperatorIntentCoordinator(_store, utcNow: () => Now,
                decisions: CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
                goalStateVersionResolver: _ => StateVersion);
            _driver = new ConductorDriver(
                getFacts: _ => GoalLifecycleFacts.None with { WorkspaceExists = true },
                getRunningPaidWorkerCount: () => 0,
                createWorkspace: _ => throw new InvalidOperationException("The workspace already exists."),
                dispatchAndStart: _ =>
                {
                    DeliveredFeedback.Add(Task.CriterionRetryFeedback.ToArray());
                    kernel.RecordTaskDispatch(Goal.Id, Task.Id,
                        new TaskDispatchRecord("test-worker", "test-worker", _root, Now));
                    return DispatchStartOutcome.Started();
                },
                startRecordedDispatches: null,
                buildServerShutdown: null,
                runAcceptanceVerification: _ => throw new InvalidOperationException("The cohort already failed."),
                runAdvisorySemanticAcceptance: null,
                retryTask: null,
                recordTaskNote: (goal, task, note) => kernel.RecordTaskNote(goal, task, note),
                recordCriterionRetryFeedback: null,
                clearCriterionRetryFeedback: null,
                rebaseOntoMain: _ => throw new InvalidOperationException("A retry must not rebase."),
                land: (_, _) => throw new InvalidOperationException("A failed cohort must not land."),
                afterSuccessfulLanding: null,
                record: _ => { },
                cleanup: _ => throw new InvalidOperationException("A retry must not clean up."),
                writeEscalation: (_, _, reason) => throw new InvalidOperationException(reason),
                classifyChangeRisk: _ => null,
                retryTaskWithCause: (goal, task, message, round, cause) =>
                {
                    Assert.Null(round);
                    Assert.Equal(RetryCause.NewTestFinding, cause);
                    Now += RetryElapsed;
                    return kernel.RetryTaskAutomatically(goal, task, message, cause, retryRoundKind: round);
                },
                utcNow: () => Now);
            _driver.CohortAttributionRevisionReader = _ => new(Candidate, Main);
        }

        internal ConductorAdvanceResult Advance() => _driver.AdvanceOnce(Goal, Policy);

        internal async Task<OperatorIntentRecord> Route(string text)
        {
            var id = Guid.NewGuid().ToString("N");
            var payload = new AdjudicateOperatorIntentPayload("route", text, ["cohort-diagnosis"],
                StateVersion, _root, Cause: nameof(RetryCause.NewSourceFinding));
            var intent = await _store.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.Adjudicate,
                Goal.Id.Value, Task.Id.Value, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), [],
                "operator", "cli", "local-process", Now, ActorKind: OperatorActorKind.Human));
            _coordinator.ExecutePending(_kernel, Goal);
            await InfrastructureTestSupport.CreateMigratedStateRepository(Path.Combine(_root, "state.db"))
                .SaveAsync(_kernel);
            _coordinator.CompletePersisted([Goal.Id]);
            return (await _store.GetAsync(intent.Id))!;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
