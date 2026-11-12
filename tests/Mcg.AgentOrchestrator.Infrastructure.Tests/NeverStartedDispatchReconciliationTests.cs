using System.Text.Json;
using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class NeverStartedDispatchReconciliationTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("corrupt")]
    public void FindingEvidenceHoldRetainsPreparedDispatchAfterReload(string original)
    {
        using var fixture = new FreshFindingEvidenceFixture();
        var receipt = fixture.Receipt();
        fixture.Attach(receipt);
        Assert.True(fixture.Select(out var claimed, out var reason), reason);
        fixture.Kernel.RecordFindingEvidenceReviewContinuation(fixture.Goal.Id, fixture.Reviewer.Id, claimed!);
        fixture.Kernel.RetryTaskAutomatically(fixture.Goal.Id, fixture.Reviewer.Id, claimed!.Render(), RetryCause.NewTestFinding);
        var dispatch = new TaskDispatchRecord("fixture", "review", fixture.Root, FreshFindingEvidenceFixture.Now.AddMinutes(10),
            CandidateIdentity: fixture.Identity, FindingEvidenceReviewContinuation: claimed);
        fixture.Kernel.RecordTaskDispatch(fixture.Goal.Id, fixture.Reviewer.Id, dispatch);
        fixture.Attach(fixture.Receipt(id: "equivalent-copy"));
        var path = receipt.Arms!.Single().TestResultPaths!.Single();
        if (original == "missing") File.Delete(path);
        else File.AppendAllText(path, "corrupt");
        fixture.Reload();
        var restoredDispatch = fixture.Reviewer.LastDispatch;
        Assert.False(NeverStartedDispatchReconciliation.CanRecover(fixture.Goal, fixture.Reviewer));
        var driver = fixture.Driver();

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(
            driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive).Outcome);

        Assert.Equal("Finding evidence continuation held: prepared dispatch evidence no longer qualifies.", held.Reason);
        Assert.Equal("claimed-review-evidence-unavailable", held.Decision!.DiscriminatingEvidence);
        Assert.Equal(WorkTaskStatus.Running, fixture.Reviewer.Status);
        Assert.Same(restoredDispatch, fixture.Reviewer.LastDispatch);
        Assert.Equal(dispatch.DispatchedAt, fixture.Reviewer.LastDispatch!.DispatchedAt);
        Assert.Null(fixture.Reviewer.LastProcess);
        Assert.Equal(0, fixture.Launches);
    }

    // Parallel-safe: all files and SQLite stores are private; worker launches are forbidden.
    [Theory]
    [InlineData("retired-model")]
    [InlineData("developer-test-lane")]
    public void DeliberateStartHoldKeepsPreparationAndOriginalRefusal(string mechanism)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root, root);
            var kernel = new AgentOrchestratorKernel();
            var task = new TaskSpec(TaskId.New(), "Implement widget", AgentRole.Developer);
            var goal = kernel.CreateGoal(mechanism == "developer-test-lane"
                ? "Improve widget.\n## Scope\nAdd new test class BuildWidgetTests." : "Improve widget.", [task]);
            kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Improve widget", ["Widget handles input"],
                VerificationClass.TestVerifiable, [], []));
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            Directory.CreateDirectory(Path.Combine(root, "config"));
            File.WriteAllText(Path.Combine(root, "config", "acceptance-manifest.json"), """
                { "version": 1, "engine": { "infrastructureTestLanes": [
                  { "name": "Build lane", "filter": "FullyQualifiedName~Build", "ownedCollections": ["BuildCollection"] },
                  { "name": "Remainder", "filter": "FullyQualifiedName!~Build" }
                ] } }
                """);
            var dispatch = new TaskDispatchRecord("fixture", "fixture", root, DateTimeOffset.Parse("2026-11-11T21:30:00Z"),
                ProviderName: "OpenAI", ModelName: mechanism == "retired-model" ? "gpt-6-sol" : "gpt-6.1-sol");
            kernel.RecordTaskDispatch(goal.Id, task.Id, dispatch);
            var prepared = task.LastDispatch;
            var driver = ConductorDriverTests.MakeDriver();
            Assert.False(NeverStartedDispatchReconciliation.CanRecover(goal, task));
            Assert.True(PreparedDispatchRecoveryEligibility.IsHeld(driver, goal, task, workspace));

            var recovery = NeverStartedDispatchReconciliation.BeforeAdvance(kernel, goal, GoalLifecycleState.Dispatched,
                ConductorAutonomyPolicy.Permissive, _ => throw new InvalidOperationException("Unexpected solo hold"),
                candidate => PreparedDispatchRecoveryEligibility.IsHeld(driver, goal, candidate, workspace));

            Assert.Null(recovery); // The start path retains ownership of the hold and its diagnostic.
            var processes = new GoalDispatchOperations(resolveHome: _ => throw new InvalidOperationException("Unexpected refresh"))
                .StartDispatches(kernel, workspace, goal, refreshBeforeStart: false,
                    runner: new BackgroundDispatchRunner(startProcess: _ => throw new InvalidOperationException("Unexpected worker")));
            var refusal = Assert.Single(processes.StartRefusals!);
            Assert.Equal(task.Id, refusal.TaskId);
            if (mechanism == "retired-model")
                Assert.Equal("Retired OpenAI model in an unstarted dispatch requires preparation refresh before start.", refusal.Reason);
            else
            {
                Assert.StartsWith("Goal readiness preflight blocked Developer dispatch. ", refusal.Reason);
                Assert.Contains(LaneSubstringCollisionCheck.Rule, refusal.Reason);
                Assert.Contains("BuildWidgetTests", refusal.Reason);
            }
            var outcome = ConductorDriver.ClassifyRecordedDispatchStartForConductor(processes);
            Assert.Equal("Dispatch recorded but no process was startable: task " + task.Id.Value[..8] + ": " + refusal.Reason,
                outcome.Reason);
            Assert.Equal(WorkTaskStatus.Running, task.Status);
            Assert.Same(prepared, task.LastDispatch);
            Assert.Null(task.LastProcess);
            Assert.Empty(processes.Tasks);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task OperatorIntentSuppressionDiscardsItsPreparationWithOriginalRefusal()
    {
        var f = new DispatchStartRecoveryTestFixture(solo: false);
        try
        {
            var dispatch = f.Prepare();
            var store = SqliteOperatorIntentStore.ForDirectories(f.Workspace.OrchestratorDirectory, f.Workspace.LogDirectory);
            await store.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                OperatorIntentVerbs.Adjudicate, f.GoalId.Value, f.TaskId.Value, "{}", [], "operator", "cli", "fixture", f.Time.UtcNow));

            var processes = new GoalDispatchOperations().StartDispatches(f.Kernel, f.Workspace, f.Goal,
                refreshBeforeStart: false,
                runner: new BackgroundDispatchRunner(startProcess: _ => throw new InvalidOperationException("Unexpected worker")));

            Assert.Equal("Pending operator intent holds task dispatch admission.", Assert.Single(processes.StartRefusals!).Reason);
            Assert.Equal(WorkTaskStatus.Assigned, f.Task.Status);
            Assert.Null(f.Task.LastDispatch);
            Assert.Null(f.Task.LastProcess);
            Assert.Contains(f.Task.DispatchHistory, item => item.DispatchedAt == dispatch.DispatchedAt);
            Assert.Empty(processes.Tasks);
        }
        finally { Directory.Delete(f.Root, recursive: true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void NextTickRecoversOnlyUnclaimedDispatchAndDoesNotRedispatch(bool claimed, bool confirmed)
    {
        var f = new DispatchStartRecoveryTestFixture(solo: claimed);
        var dispatch = f.Prepare();
        DispatchStartFailureEvidence.Record(f.Kernel, f.GoalId, f.TaskId, dispatch.DispatchedAt, "Recorded start failure");
        if (claimed) f.ClaimCurrent(confirmed);
        var driver = ConductorDriverTests.MakeDriver(getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            dispatchAndStart: _ => throw new InvalidOperationException("Recovery tick must not dispatch"),
            startRecordedDispatches: _ => throw new InvalidOperationException("Recovery tick must not start"));
        driver.BeginTick(f.Kernel, 1);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(driver.AdvanceOnce(f.Goal, ConductorAutonomyPolicy.Permissive).Outcome);

        Assert.Equal(claimed ? WorkTaskStatus.Running : WorkTaskStatus.Assigned, f.Task.Status);
        if (claimed)
        {
            Assert.Equal(dispatch.DispatchedAt, f.Task.LastDispatch!.DispatchedAt);
            Assert.Contains("solo-start-claimed-reconciliation-required", held.Reason);
        }
        else
        {
            Assert.Null(f.Task.LastDispatch);
            Assert.Contains(f.Goal.Timeline, entry => entry.TaskId == f.TaskId &&
                entry.Kind == ProgressKind.TaskNote && entry.Message.Contains("Conductor reconciled a never-started dispatch"));
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CancelDispatchIntentRecoversOnlyUnclaimedCurrentDispatch(bool claimed, bool confirmed)
    {
        var f = new DispatchStartRecoveryTestFixture(solo: claimed);
        var dispatch = f.Prepare();
        DispatchStartFailureEvidence.Record(f.Kernel, f.GoalId, f.TaskId, dispatch.DispatchedAt, "Recorded start failure");
        var store = new CancelDispatchIntentTestStore();
        var context = new CliExecutionContext(f.Kernel, f.Workspace, new InMemoryModelProviderRegistry([]),
            AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), f.Goal);
        var stopped = 0;
        CliCommandHandlers.ExecuteCancelDispatch(["cancel-dispatch", "1"], context, store, _ => stopped++);
        var intent = Assert.Single(await store.ListForGoalAsync(f.GoalId.Value));
        var payload = JsonSerializer.Deserialize<CancelDispatchOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal(0, payload.ProcessId);
        Assert.Equal(dispatch.DispatchedAt, payload.ProcessStartedAt);
        Assert.Equal(WorkTaskStatus.Running, f.Task.Status); // CLI submits; the conductor owns mutation.
        if (claimed)
        {
            f.ClaimCurrent(confirmed); // Claim after enqueue also protects the dispatch at application time.
            context = new CliExecutionContext(f.Kernel, f.Workspace, new InMemoryModelProviderRegistry([]),
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), f.Goal);
            Assert.Throws<InvalidOperationException>(() => CliCommandHandlers.ExecuteCancelDispatch(
                ["cancel-dispatch", "1"], context, store, _ => stopped++));
        }
        var coordinator = new OperatorIntentCoordinator(store)
        {
            CancelLatestProcess = (_, _, _) => throw new InvalidOperationException("No process exists to cancel")
        };
        coordinator.ExecutePending(f.Kernel, f.Goal);
        f.Save();
        coordinator.CompletePersisted([f.GoalId]);

        Assert.Equal(0, stopped);
        Assert.Equal(claimed ? WorkTaskStatus.Running : WorkTaskStatus.Assigned, f.Task.Status);
        if (claimed)
            Assert.Equal(dispatch.DispatchedAt, f.Task.LastDispatch!.DispatchedAt);
        else
        {
            Assert.Null(f.Task.LastDispatch);
            Assert.Equal(OperatorIntentStatus.Applied, (await store.GetAsync(intent.Id))!.Status);
            Assert.Contains(f.Goal.Timeline, entry => entry.TaskId == f.TaskId &&
                entry.Kind == ProgressKind.TaskNote && entry.Message.Contains("Operator cancel-dispatch reconciled"));
        }
    }

    [Fact]
    public void StaleCancellationCannotDiscardReplacementDispatch()
    {
        var f = new DispatchStartRecoveryTestFixture(solo: false);
        var old = f.Prepare();
        var payload = new CancelDispatchOperatorIntentPayload(0, old.DispatchedAt,
            BackgroundDispatchRunner.BuildDispatchId(f.GoalId, f.TaskId, old));
        f.Kernel.DiscardUnstartedPreparedDispatch(f.GoalId, f.TaskId, old.DispatchedAt, "Replace fixture preparation");
        var current = f.Prepare();
        DispatchStartFailureEvidence.Record(f.Kernel, f.GoalId, f.TaskId, current.DispatchedAt, "Recorded start failure");
        Assert.Throws<InvalidOperationException>(() => NeverStartedDispatchReconciliation.ApplyCancel(f.Kernel, f.Goal, f.Task, payload));
        Assert.Equal(WorkTaskStatus.Running, f.Task.Status);
        Assert.Equal(current.DispatchedAt, f.Task.LastDispatch!.DispatchedAt);
    }
}
