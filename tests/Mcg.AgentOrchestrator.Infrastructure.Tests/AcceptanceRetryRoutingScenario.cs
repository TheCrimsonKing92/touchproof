using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Shared fixture for the two routing contract classes. Every instance owns its stores;
// clocks, acceptance, git operations and worker launches are controlled seams.
internal sealed class AcceptanceRetryRoutingScenario : IDisposable, IClock
{
    internal const string FailingTest = "ManifestTests.MissingManifest_ThrowsAtAttemptCreation";
    internal const string Assertion = "Assert.Throws() Failure: No exception was thrown";
    internal const string AttemptId = "gate-attempt-17";
    private const long StateVersion = 7;
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mcg-routing-" + Guid.NewGuid().ToString("N"));
    internal DateTimeOffset Now { get; set; } = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);
    DateTimeOffset IClock.UtcNow => Now;
    internal AgentOrchestratorKernel Kernel { get; private set; }
    private readonly GoalId _goalId;
    internal Goal Goal => Kernel.GetGoal(_goalId);
    internal TaskSpec Task => Goal.Tasks.Single();
    internal OrchestratorWorkspace Workspace { get; }
    internal SqliteOperatorIntentStore Store { get; }
    internal OperatorIntentCoordinator Coordinator { get; }
    internal ConductorAutonomyPolicy Policy { get; set; }
    internal ConductorDriver Driver { get; private set; }
    internal int AcceptanceCalls { get; private set; }
    internal int DispatchCalls { get; private set; }
    internal List<string[]> DeliveredFeedback { get; } = [];
    internal IReadOnlyList<string> AutomaticFeedback { get; private set; } = [];

    internal AcceptanceRetryRoutingScenario(int holdSeconds = 180, int maxRetries = 3)
    {
        Directory.CreateDirectory(_root);
        Workspace = OrchestratorWorkspace.ForDirectory(_root);
        Kernel = new AgentOrchestratorKernel(this);
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel,
            ConductorDriverTests.DefaultAgents(), "Acceptance retry routing contract");
        _goalId = goal.Id;
        Kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Acceptance retry routing contract",
            ["Preserve the missing-manifest throw"], VerificationClass.TestVerifiable, [], []));
        Policy = ConductorAutonomyPolicy.Conservative with
        {
            AutomaticRetryOperatorRoutingHoldSeconds = holdSeconds,
            MaxCriterionRetries = maxRetries
        };
        Store = new SqliteOperatorIntentStore(Path.Combine(Workspace.OrchestratorDirectory,
            SqliteOperatorIntentStore.DatabaseFileName), Workspace.LogDirectory, utcNow: () => Now);
        Coordinator = new OperatorIntentCoordinator(Store, utcNow: () => Now,
            decisions: CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory),
            goalStateVersionResolver: _ => StateVersion);
        Driver = CreateDriver();
    }

    internal ConductorAdvanceResult Advance() => Driver.AdvanceOnce(Goal, Policy);

    internal void FailGate()
    {
        if (Task.Status == WorkTaskStatus.Assigned)
            Kernel.RecordTaskDispatch(Goal.Id, Task.Id,
                new TaskDispatchRecord("test-worker", "test-worker", _root, Now.AddSeconds(-1)));
        Kernel.RecordTaskVerification(Goal.Id, Task.Id,
            new TaskVerificationRecord("test-worker", _root, 0, "ok", "", Now));
        var previousCalls = AcceptanceCalls;
        Xunit.Assert.IsType<ConductorAdvanceOutcome.Executed>(Advance().Outcome);
        Xunit.Assert.Equal(previousCalls + 1, AcceptanceCalls);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, Task.Status);
        Xunit.Assert.NotNull(Task.AcceptanceRetryRoutingHold);
    }

    internal async Task<OperatorIntentRecord> Route(string text, string evidence = "diagnosis-receipt")
    {
        var id = Guid.NewGuid().ToString("N");
        var payload = new AdjudicateOperatorIntentPayload("route", text, [evidence],
            StateVersion, _root, Cause: nameof(RetryCause.NewSourceFinding));
        var intent = await Store.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.Adjudicate,
            Goal.Id.Value, Task.Id.Value, JsonSerializer.Serialize(payload, JsonSerializerOptions.Web), [],
            "operator", "cli", "local-process", Now, ActorKind: OperatorActorKind.Human));
        Coordinator.ExecutePending(Kernel, Goal);
        await InfrastructureTestSupport.CreateMigratedStateRepository(Path.Combine(_root, "state.db"))
            .SaveAsync(Kernel);
        Coordinator.CompletePersisted([Goal.Id]);
        return (await Store.GetAsync(intent.Id))!;
    }

    internal TaskIntentAdmissionReference PrepareAdmission()
    {
        var reference = Store.CaptureAdmission(Goal.Id.Value, Task.Id.Value) with { DispatchedAt = Now };
        Store.BindPreparedAdmission(reference, Now);
        return reference;
    }

    internal async Task Recover()
    {
        var repository = InfrastructureTestSupport.CreateMigratedStateRepository(Path.Combine(_root, "state.db"));
        await repository.SaveAsync(Kernel);
        var loaded = await repository.LoadGoalsAsync([Goal.Id]);
        Kernel = AgentOrchestratorKernel.FromSnapshot(loaded.ExportSnapshot(), this);
        Driver = CreateDriver();
    }

    private ConductorDriver CreateDriver() => new(
        getFacts: _ => GoalLifecycleFacts.None with { WorkspaceExists = true },
        getRunningPaidWorkerCount: () => 0,
        createWorkspace: _ => throw new InvalidOperationException("The workspace already exists."),
        dispatchAndStart: _ =>
        {
            DispatchCalls++;
            DeliveredFeedback.Add(Task.CriterionRetryFeedback.ToArray());
            Kernel.RecordTaskDispatch(Goal.Id, Task.Id,
                new TaskDispatchRecord("test-worker", "test-worker", _root, Now));
            return DispatchStartOutcome.Started();
        },
        startRecordedDispatches: null,
        buildServerShutdown: null,
        runAcceptanceVerification: _ =>
        {
            AcceptanceCalls++;
            var check = new AcceptanceCheckResult("infrastructure tests", false, 1,
                $"[FAIL] {FailingTest}{Environment.NewLine}{Assertion}",
                FailingTestIdentities: [FailingTest],
                TestProjectPath: "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Mcg.AgentOrchestrator.Infrastructure.Tests.csproj");
            return new AcceptanceVerificationSummary(false, [check], FailedChecks: [check.Name],
                BranchHeadSha: "candidate-a", MainHeadSha: "main-a", AttemptId: AttemptId);
        },
        runAdvisorySemanticAcceptance: null,
        retryTask: null,
        recordTaskNote: (goalId, taskId, note) => Kernel.RecordTaskNote(goalId, taskId, note),
        recordCriterionRetryFeedback: (goalId, taskId, feedback) =>
        {
            AutomaticFeedback = feedback.ToArray();
            return Kernel.RecordCriterionRetryFeedback(goalId, taskId, feedback);
        },
        clearCriterionRetryFeedback: null,
        rebaseOntoMain: _ => new GoalWorktreeRebaseResult(
            GoalWorktreeRebaseStatus.AlreadyFastForwardable, "goal/test", "ready", [], null),
        land: (_, _) => throw new InvalidOperationException("A failed gate must not land."),
        afterSuccessfulLanding: null,
        record: _ => { },
        cleanup: _ => throw new InvalidOperationException("A failed gate must not clean up."),
        writeEscalation: (_, _, reason) => throw new InvalidOperationException(reason),
        classifyChangeRisk: _ => null,
        retryTaskWithCause: (goalId, taskId, message, round, cause) =>
            Kernel.RetryTask(goalId, taskId, message, retryRoundKind: round, retryCause: cause),
        utcNow: () => Now);

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
