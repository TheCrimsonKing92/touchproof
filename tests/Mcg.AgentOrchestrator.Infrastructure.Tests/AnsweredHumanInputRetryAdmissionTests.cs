using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory goals and a private SQLite root per row; no process is launched.
public sealed class AnsweredHumanInputRetryAdmissionTests
{
    [Xunit.Theory]
    [Xunit.InlineData("author", "conductor-author", OperatorActorKind.Agent)]
    [Xunit.InlineData("operator", "cli", OperatorActorKind.Human)]
    public async Task AuthoritativeAnswer_NextDispatch_IsAdmittedWithoutAnotherQuestion(
        string actor, string channel, OperatorActorKind actorKind)
    {
        var root = Path.Combine(Path.GetTempPath(), $"answered-retry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var clock = new TestClock();
            var kernel = new AgentOrchestratorKernel(clock);
            var task = new TaskSpec(TaskId.New(), "Implement clarified contract", AgentRole.Developer);
            var goal = kernel.CreateGoal("Resume after authoritative answer", [task]);
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var before = Fingerprint(kernel, goal, task);
            var startedAt = clock.UtcNow;
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("fixture", "unused", root, startedAt));
            Xunit.Assert.Equal(RetryAdmissionDecision.Allowed, kernel.RecordPreparedRetryAdmission(
                goal.Id, task.Id, before, PaidRouteClassification.Paid, startedAt).Decision);
            kernel.RecordTaskProcessStarted(goal.Id, task.Id,
                new TaskProcessRecord(4101, "unused", root, "out", "err", "exit",
                    startedAt, startedAt.AddSeconds(1), 1));
            var request = kernel.RequestHumanInput(goal.Id, task.Id, "Which contract?");
            clock.Advance();
            kernel.RecordTaskVerification(goal.Id, task.Id,
                new TaskVerificationRecord("worker", root, 1, "", "Clarification needed.",
                    clock.UtcNow, DispatchStartedAt: startedAt));
            // Keep feedback/candidate constant so only the answer can enable admission.
            task.RecordRetry(clock.UtcNow, RetryCause.ContractClarification);
            Xunit.Assert.Equal(before, Fingerprint(kernel, goal, task));
            Xunit.Assert.False(request.IsCompleted);
            Xunit.Assert.Equal(WorkTaskStatus.WaitingForHuman, task.Status);

            var workspace = OrchestratorWorkspace.ForDirectory(root);
            var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
            var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
                request.Id.Value, goal.Id.Value, "Use the existing contract.", actorKind);
            var intent = await intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
                Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, goal.Id.Value, null,
                JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], actor, channel,
                "local-process", clock.UtcNow, ActorKind: actorKind))
                .WaitAsync(TimeSpan.FromSeconds(30)); // Hang guard for persisting the answer intent.
            var coordinator = new OperatorIntentCoordinator(intents,
                decisions: CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory),
                goalStateVersionResolver: _ => 0);

            Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
            coordinator.CompletePersisted([goal.Id]);
            Xunit.Assert.Equal(OperatorIntentStatus.Applied,
                (await intents.GetAsync(intent.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);
            Xunit.Assert.True(request.IsCompleted);
            Xunit.Assert.Equal(payload.Text, request.AuthoritativeAnswer?.Text);
            Xunit.Assert.Equal(WorkTaskStatus.Assigned, task.Status);
            var answered = Fingerprint(kernel, goal, task);
            Xunit.Assert.NotEqual(before, answered);
            clock.Advance();
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("fixture", "unused", root, clock.UtcNow));

            var admission = kernel.RecordPreparedRetryAdmission(
                goal.Id, task.Id, answered, PaidRouteClassification.Paid, clock.UtcNow);

            Xunit.Assert.Equal(RetryAdmissionDecision.Allowed, admission.Decision);
            Xunit.Assert.Null(task.RetryAdmissionHoldRoute);
            Xunit.Assert.Equal(new HumanInputRequestCounts(1, 0),
                kernel.GetHumanInputRequestCounts(goal.Id, task.Id));
            Xunit.Assert.Empty(kernel.GetPendingHumanInput(goal.Id));
            Xunit.Assert.DoesNotContain(goal.Timeline, item =>
                item.TaskId == task.Id && item.Kind == ProgressKind.NoProgressRedispatchPrevented);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static RetryContextFingerprint Fingerprint(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task) =>
        RetryContextFingerprintFactory.Build(goal, task, "fixture", "model", PaidRouteClassification.Paid,
            null, null, null, kernel.HumanInputRequests);

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; private set; } = new(2026, 11, 12, 12, 0, 0, TimeSpan.Zero);
        public void Advance() => UtcNow = UtcNow.AddSeconds(1);
    }
}
