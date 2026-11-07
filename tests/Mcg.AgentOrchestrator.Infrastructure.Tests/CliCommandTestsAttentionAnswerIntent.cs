using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class CliCommandTestsAttentionAnswerIntent : CliCommandTestBase
{
    private protected override bool UsesGit => false;

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task Amendment_decline_flag_routes_initial_and_late_answers(bool late)
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("Decline a Planner amendment through attention", [planner]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, planner.Id, "Request a criterion amendment",
            kind: HumanWaitKind.PlannerBriefAmendment, amendmentCriterionIndex: 1).Request;
        if (late) kernel.SubmitHumanInput(request.Id, "Accept the proposed text.");
        const string answer = "Keep the current criterion.\nUse the current brief.";

        ExecuteCliAndCapture(["attention", "answer", goal.Id.Value[..8], request.Id.Value[..8], answer,
            "--amendment", "decline"], kernel, workspace);

        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var pending = Xunit.Assert.Single(await intents.ListForGoalAsync(goal.Id.Value));
        var payload = System.Text.Json.JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(pending.PayloadJson, OperatorIntentJson.Options);
        Xunit.Assert.Equal(PlannerBriefAmendmentDisposition.Decline, payload!.AmendmentDisposition);
        Xunit.Assert.Equal(answer, payload.Text);
        var coordinator = OperatorIntentCoordinator.CreateDefault(workspace);
        Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
        coordinator.CompletePersisted([goal.Id]);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied, (await intents.GetAsync(pending.Id))!.Status);
        Xunit.Assert.Equal(WorkTaskStatus.Assigned, planner.Status);
        Xunit.Assert.Equal(1, goal.AuthoritativeBrief.Version);
        Xunit.Assert.Null(kernel.GetPlannerBriefAmendmentRevisionPending(goal.Id));
        Xunit.Assert.Contains(answer, Xunit.Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried)).Message);
        Xunit.Assert.Equal(late ? 2 : 1, request.AnswerHistory.Count);
    }

    [Xunit.Fact]
    public async Task Amendment_flag_without_payload_is_rejected_before_enqueue()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Ordinary clarification is not an amendment");
        var request = kernel.RequestHumanInput(goal.Id, null, "Ordinary clarification");

        var error = Xunit.Assert.Throws<ArgumentException>(() => ExecuteCliAndCapture(
            ["attention", "answer", request.Id.Value[..8], "Decline", "--amendment", "decline"], kernel, workspace));

        Xunit.Assert.Contains("requires a request carrying a Planner brief amendment payload", error.Message);
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        Xunit.Assert.Empty(await intents.ListForGoalAsync(goal.Id.Value));
        Xunit.Assert.False(request.IsCompleted);
    }

    [Xunit.Fact]
    public async Task Attention_answer_queues_until_tick_then_rejects_answered_item()
    {
        var root = CreateTempDirectory();
        var workspace = CreateRefinedWorkspace(root);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Answer by intent");
        var collaboration = CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var item = await collaboration.RaiseAsync(CollaborationItemType.Clarification, goal.Id.Value,
            "Scope", "Which scope?", $"spec-clarification:{goal.Id.Value}:scope:answer-intent")
            .WaitAsync(TimeSpan.FromSeconds(30));

        var output = ExecuteCliAndCapture(
            ["attention", "answer", goal.Id.Value[..8], item.Id[..8], "Use selected scope"],
            kernel, workspace);
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var pending = Xunit.Assert.Single(await intents.ListForGoalAsync(goal.Id.Value).WaitAsync(TimeSpan.FromSeconds(30)));
        Xunit.Assert.Contains($"id={pending.Id} verb=answer", output);
        Xunit.Assert.Contains("status=Pending", output);
        Xunit.Assert.Equal(OperatorIntentStatus.Pending, pending.Status);
        Xunit.Assert.Equal(OperatorActorKind.Human, pending.ActorKind);
        Xunit.Assert.Equal(CollaborationItemStatus.Raised,
            (await collaboration.ListAsync(goal.Id.Value).WaitAsync(TimeSpan.FromSeconds(30))).Single().Status);

        var coordinator = OperatorIntentCoordinator.CreateDefault(workspace);
        Xunit.Assert.True(coordinator.ExecutePending(kernel, goal).MutatedGoalState);
        coordinator.CompletePersisted([goal.Id]);
        Xunit.Assert.Equal(OperatorIntentStatus.Applied,
            (await intents.GetAsync(pending.Id).WaitAsync(TimeSpan.FromSeconds(30)))!.Status);

        var duplicateOutput = ExecuteCliAndCapture(
            ["attention", "answer", "--actor-kind", "Agent", "--operator-actor", "author-agent",
                goal.Id.Value[..8], item.Id[..8], "Another scope"],
            kernel, workspace);
        Xunit.Assert.Contains("status=Pending", duplicateOutput);
        var duplicate = (await intents.ListForGoalAsync(goal.Id.Value).WaitAsync(TimeSpan.FromSeconds(30)))
            .Single(intent => intent.Id != pending.Id);
        Xunit.Assert.Equal(OperatorActorKind.Agent, duplicate.ActorKind);
        coordinator.ExecutePending(kernel, goal);
        var rejected = await intents.GetAsync(duplicate.Id).WaitAsync(TimeSpan.FromSeconds(30));
        Xunit.Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
        Xunit.Assert.Contains("already answered", rejected.Outcome);
        Xunit.Assert.Null(await collaboration.GetDecisionStateAsync($"answer-{duplicate.Id}")
            .WaitAsync(TimeSpan.FromSeconds(30)));
    }
}
