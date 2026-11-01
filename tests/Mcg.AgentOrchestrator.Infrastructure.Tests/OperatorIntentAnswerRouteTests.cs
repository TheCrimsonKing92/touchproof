using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class OperatorIntentAnswerRouteTests
{
    [Theory]
    [InlineData(null, RetryCause.ContractClarification)]
    [InlineData("NewSourceFinding", RetryCause.NewSourceFinding)]
    public async Task Cli_answer_routes_like_adjudication(string? cause, RetryCause expected)
    {
        using var routed = new Harness();
        using var direct = new Harness();
        var intent = await routed.CliAnswer(cause is null ? ["--route", "developer"] :
            ["--route", "developer", "--cause", cause]);
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Equal("developer", payload.RouteRole);
        Assert.Equal(expected.ToString(), payload.RouteCause);
        direct.Kernel.SubmitHumanInput(direct.Request.Id, Harness.Guidance);
        var adjudication = await direct.Enqueue(OperatorIntentVerbs.Adjudicate,
            new AdjudicateOperatorIntentPayload("route", Harness.Guidance, ["receipt-1"], 0, "", expected.ToString()),
            direct.Developer.Id.Value);

        routed.Apply();
        direct.Apply();

        Assert.True(routed.Request.IsCompleted);
        Assert.Equal(Harness.Guidance, routed.Request.Answer);
        Assert.Equal(WorkTaskStatus.Assigned, routed.Developer.Status);
        Assert.Equal(direct.Developer.Status, routed.Developer.Status);
        Assert.Equal(expected, routed.Developer.PendingRetryCause);
        Assert.Equal(direct.Developer.PendingRetryCause, routed.Developer.PendingRetryCause);
        Assert.Equal(Harness.Guidance, routed.Developer.AcceptedRetryFeedback?.Message);
        Assert.Equal(direct.Developer.AcceptedRetryFeedback?.Message, routed.Developer.AcceptedRetryFeedback?.Message);
        Assert.Equal(OperatorIntentStatus.Applied, (await routed.Intents.GetAsync(intent.Id))!.Status);
        Assert.Equal(EffectReceiptStatus.Applied, (await routed.Decisions.GetDecisionStateAsync($"answer-{intent.Id}"))!.Effect!.Status);
        var route = await routed.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}");
        Assert.Equal(EffectReceiptStatus.Applied, route!.Effect!.Status);
        Assert.Contains(route.Receipt!.EvidenceHashes, entry => entry.ReceiptId == "receipt-1");
        Assert.Equal(EffectReceiptStatus.Applied, (await direct.Decisions.GetDecisionStateAsync($"adjudicate-{adjudication.Id}"))!.Effect!.Status);
        Assert.Single(routed.Goal.Timeline.Where(entry => entry.OperatorIntentApplied?.IntentId == intent.Id));
    }

    [Theory]
    [InlineData(WorkTaskStatus.Running)]
    [InlineData(WorkTaskStatus.WaitingForHuman)]
    public async Task Ineligible_developer_rejects_before_answer(WorkTaskStatus status)
    {
        using var harness = new Harness();
        if (status == WorkTaskStatus.WaitingForHuman)
            harness.Kernel.RequestHumanInput(harness.Goal.Id, harness.Developer.Id, "Developer question?");
        else
            harness.Kernel.ReportTaskProgress(harness.Goal.Id, harness.Developer.Id, status, "running");
        var before = JsonSerializer.Serialize(harness.Kernel.ExportSnapshot());
        var intent = await harness.EnqueueAnswer();

        Assert.False(harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal).MutatedGoalState);

        var rejected = await harness.Intents.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
        Assert.Contains("task-not-retryable", rejected.Outcome);
        Assert.False(harness.Request.IsCompleted);
        Assert.Null(await harness.Decisions.GetDecisionStateAsync($"answer-{intent.Id}"));
        Assert.Null(await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}"));
        Assert.Equal(before, JsonSerializer.Serialize(harness.Kernel.ExportSnapshot()));
    }

    [Fact]
    public async Task Already_answered_request_routes_without_replacing_answer()
    {
        using var harness = new Harness();
        harness.Kernel.SubmitHumanInput(harness.Request.Id, "Earlier default");
        var intent = await harness.EnqueueAnswer();

        harness.Apply();

        Assert.Equal("Earlier default", harness.Request.Answer);
        Assert.Equal(Harness.Guidance, harness.Developer.AcceptedRetryFeedback?.Message);
        Assert.Equal(RetryCause.ContractClarification, harness.Developer.PendingRetryCause);
        Assert.Null(await harness.Decisions.GetDecisionStateAsync($"answer-{intent.Id}"));
        Assert.Equal(EffectReceiptStatus.Applied, (await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}"))!.Effect!.Status);
        var applied = await harness.Intents.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Applied, applied!.Status);
        Assert.Contains("answer-already-recorded; routed developer", applied.Outcome);
    }

    [Fact]
    public async Task Unrouted_answer_preserves_developer_and_resumes_reviewer()
    {
        using var harness = new Harness();
        var before = JsonSerializer.Serialize(harness.Kernel.ExportSnapshot().Goals.Single().Tasks
            .Single(task => task.Id == harness.Developer.Id.Value));
        var intent = await harness.CliAnswer([]);
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options)!;
        Assert.Null(payload.RouteRole);
        Assert.Null(payload.RouteCause);

        harness.Apply();

        Assert.True(harness.Request.IsCompleted);
        Assert.Equal(Harness.Guidance, harness.Request.Answer);
        Assert.Equal(WorkTaskStatus.Assigned, harness.Reviewer.Status);
        Assert.Equal(before, JsonSerializer.Serialize(harness.Kernel.ExportSnapshot().Goals.Single().Tasks
            .Single(task => task.Id == harness.Developer.Id.Value)));
        Assert.Equal(EffectReceiptStatus.Applied, (await harness.Decisions.GetDecisionStateAsync($"answer-{intent.Id}"))!.Effect!.Status);
        Assert.Null(await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}"));
        Assert.Equal(OperatorIntentStatus.Applied, (await harness.Intents.GetAsync(intent.Id))!.Status);
    }

    [Fact]
    public void Legacy_json_has_null_route_fields()
    {
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(
            """{"targetKind":2,"targetId":"request","goalId":"goal","text":"answer","actorKind":1}""",
            OperatorIntentJson.Options)!;
        Assert.Null(payload.RouteRole);
        Assert.Null(payload.RouteCause);
    }

    [Fact]
    public void Help_mentions_developer_route_and_cause()
    {
        Assert.Contains("[--route developer [--cause <RetryCause>]]", CliCommandHelp.AttentionUsage);
    }

    [Theory]
    [InlineData("--cause", "NewSourceFinding")]
    [InlineData("--route", "reviewer")]
    [InlineData("--route", "developer", "--cause", "Unknown")]
    [InlineData("--route", "developer", "--cause", "bogus")]
    public async Task Invalid_cli_route_is_not_enqueued(params string[] flags)
    {
        using var harness = new Harness();
        Assert.Throws<ArgumentException>(() => harness.RunCli(flags));
        Assert.Null(await harness.Intents.ClaimNextAsync(harness.Goal.Id.Value, OperatorIntentCoordinator.ClaimOwner));
        Assert.False(harness.Request.IsCompleted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Clarification_answer_can_route_when_open_or_already_answered(bool answered)
    {
        using var harness = new Harness();
        harness.Kernel.SubmitHumanInput(harness.Request.Id, "Earlier reviewer answer");
        var key = $"spec-clarification:{harness.Goal.Id.Value}:scope";
        var item = await harness.Decisions.RaiseAsync(CollaborationItemType.Clarification,
            harness.Goal.Id.Value, "Scope", "Which scope?", key);
        if (answered)
            Assert.True(await harness.Decisions.TryResolveAsync(key, "Earlier default", briefVersion: 1));
        var resolverCalls = 0;
        var coordinator = new OperatorIntentCoordinator(harness.Intents, decisions: harness.Decisions,
            goalStateVersionResolver: _ => 0, clarificationAnswers: async (correlation, text, version) =>
            {
                resolverCalls++;
                return await harness.Decisions.TryResolveAsync(correlation, text, briefVersion: version);
            });
        var intent = await harness.Enqueue(OperatorIntentVerbs.Answer,
            new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.Clarification, item.Id,
                harness.Goal.Id.Value, Harness.Guidance, OperatorActorKind.Human, ["receipt-1"], RouteRole: "developer"));
        Assert.False(OperatorIntentBatchPolicy.IsClarificationAnswer(intent));

        Assert.True(coordinator.ExecutePending(harness.Kernel, harness.Goal).MutatedGoalState);
        coordinator.CompletePersisted([harness.Goal.Id]);

        var resolved = (await harness.Decisions.ListAsync(harness.Goal.Id.Value)).Single(row => row.Id == item.Id);
        Assert.Equal(answered ? "Earlier default" : Harness.Guidance, resolved.Resolution);
        Assert.Equal(answered ? 0 : 1, resolverCalls);
        Assert.Equal(RetryCause.ContractClarification, harness.Developer.PendingRetryCause);
        Assert.Equal(Harness.Guidance, harness.Developer.AcceptedRetryFeedback?.Message);
        Assert.Equal(OperatorIntentStatus.Applied, (await harness.Intents.GetAsync(intent.Id))!.Status);
    }

    [Theory]
    [InlineData("before-answer")]
    [InlineData("after-answer")]
    [InlineData("after-route")]
    public async Task Replay_restores_answer_and_route_from_uncheckpointed_goal(string interruption)
    {
        using var harness = new Harness();
        var snapshot = harness.Kernel.ExportSnapshot();
        if (interruption == "after-answer")
        {
            var answered = AgentOrchestratorKernel.FromSnapshot(snapshot);
            answered.SubmitHumanInput(harness.Request.Id, Harness.Guidance);
            snapshot = answered.ExportSnapshot();
        }
        var intent = await harness.EnqueueAnswer();
        Assert.True(harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal).MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Claimed, (await harness.Intents.GetAsync(intent.Id))!.Status);
        if (interruption == "after-route")
        {
            // Keep both effects, simulating interruption before the final intent marker.
            snapshot = harness.Kernel.ExportSnapshot();
            snapshot = snapshot with { Goals = snapshot.Goals.Select(goal => goal with
            {
                // RetryTaskCore and Append read the clock separately. Preserve a later event
                // deterministically so replay cannot rely on equal timestamps.
                Timeline = goal.Timeline.Where(entry => entry.OperatorIntentApplied?.IntentId != intent.Id)
                    .Select(entry => entry.Kind == ProgressKind.TaskRetried && entry.TaskId == harness.Developer.Id.Value
                        ? entry with { OccurredAt = harness.Developer.LatestRetryAt!.Value.AddTicks(1) }
                        : entry).ToArray()
            }).ToArray() };
        }
        var restored = AgentOrchestratorKernel.FromSnapshot(snapshot);
        var goal = restored.GetGoal(harness.Goal.Id);
        var coordinator = new OperatorIntentCoordinator(harness.Intents, decisions: harness.Decisions,
            goalStateVersionResolver: _ => 0);

        Assert.True(coordinator.ExecutePending(restored, goal).MutatedGoalState);
        coordinator.CompletePersisted([goal.Id]);

        Assert.True(restored.HumanInputRequests.Single().IsCompleted);
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        Assert.Equal(RetryCause.ContractClarification, developer.PendingRetryCause);
        Assert.Equal(Harness.Guidance, developer.AcceptedRetryFeedback?.Message);
        Assert.Single(goal.Timeline.Where(entry => entry.Kind == ProgressKind.TaskRetried && entry.TaskId == developer.Id));
        if (interruption == "after-route")
            Assert.Equal(harness.Developer.LatestRetryAt, developer.LatestRetryAt);
        Assert.Equal(OperatorIntentStatus.Applied, (await harness.Intents.GetAsync(intent.Id))!.Status);
    }

    [Theory]
    [InlineData(false, "local-process", "ContractClarification", "missing-evidence")]
    [InlineData(true, "remote", "ContractClarification", "required tier 'Mutate'")]
    [InlineData(true, "local-process", "bogus", "unknown-retry-cause")]
    public async Task Shared_preflight_rejects_without_domain_effects(
        bool evidence, string assurance, string cause, string reason)
    {
        using var harness = new Harness();
        var before = JsonSerializer.Serialize(harness.Kernel.ExportSnapshot());
        var intent = await harness.Intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"),
            Guid.NewGuid().ToString("N"), OperatorIntentVerbs.Answer, harness.Goal.Id.Value, null,
            JsonSerializer.Serialize(new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput,
                harness.Request.Id.Value, harness.Goal.Id.Value, Harness.Guidance, OperatorActorKind.Human,
                evidence ? ["receipt-1"] : [], RouteRole: "developer", RouteCause: cause), OperatorIntentJson.Options),
            [], "operator", "cli", assurance, DateTimeOffset.UtcNow));

        Assert.False(harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal).MutatedGoalState);

        var rejected = await harness.Intents.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
        Assert.Contains(reason, rejected.Outcome);
        Assert.Equal(before, JsonSerializer.Serialize(harness.Kernel.ExportSnapshot()));
        Assert.Null(await harness.Decisions.GetDecisionStateAsync($"answer-{intent.Id}"));
        Assert.Null(await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}"));
    }

    [Fact]
    public async Task Missing_developer_rejects_before_answer()
    {
        using var harness = new Harness(includeDeveloper: false);
        var intent = await harness.EnqueueAnswer();

        Assert.False(harness.Coordinator.ExecutePending(harness.Kernel, harness.Goal).MutatedGoalState);

        Assert.False(harness.Request.IsCompleted);
        var rejected = await harness.Intents.GetAsync(intent.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
        Assert.Contains("Developer task was not found", rejected.Outcome);
        Assert.Null(await harness.Decisions.GetDecisionStateAsync($"answer-{intent.Id}"));
        Assert.Null(await harness.Decisions.GetDecisionStateAsync($"adjudicate-{intent.Id}"));
    }

    // Each harness owns a unique SQLite directory and kernel; no shared process or DB state.
    private sealed class Harness : IDisposable
    {
        internal const string Guidance = "Restore the required configuration key.";
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"answer-route-{Guid.NewGuid():N}");
        internal OrchestratorWorkspace Workspace { get; }
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal Goal { get; }
        internal TaskSpec Developer { get; } = new(TaskId.New(), "Implement", AgentRole.Developer);
        internal TaskSpec Reviewer { get; } = new(TaskId.New(), "Review", AgentRole.Reviewer);
        internal HumanInputRequest Request { get; }
        internal CollaborationItemStore Decisions { get; }
        internal SqliteOperatorIntentStore Intents { get; }
        internal OperatorIntentCoordinator Coordinator { get; }

        internal Harness(bool includeDeveloper = true)
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            Goal = Kernel.CreateGoal("Apply the ruling", includeDeveloper ? [Developer, Reviewer] : [Reviewer]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            if (includeDeveloper)
                Kernel.ReportTaskProgress(Goal.Id, Developer.Id, WorkTaskStatus.Completed, "candidate ready");
            Request = Kernel.RequestHumanInput(Goal.Id, Reviewer.Id, "Restore the key?");
            Decisions = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
            Intents = SqliteOperatorIntentStore.ForDirectories(Workspace.OrchestratorDirectory, Workspace.LogDirectory);
            Coordinator = new OperatorIntentCoordinator(Intents, decisions: Decisions, goalStateVersionResolver: _ => 0);
        }

        internal Task<OperatorIntentRecord> EnqueueAnswer() => Enqueue(OperatorIntentVerbs.Answer,
            new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput, Request.Id.Value,
                Goal.Id.Value, Guidance, OperatorActorKind.Human, ["receipt-1"], RouteRole: "developer"));

        internal Task<OperatorIntentRecord> Enqueue<T>(string verb, T payload, string? taskId = null) =>
            Intents.EnqueueAsync(new OperatorIntentRecord(Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                verb, Goal.Id.Value, taskId, JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [],
                "operator", "cli", "local-process", DateTimeOffset.UtcNow));

        internal void RunCli(string[] flags)
        {
            var textPath = Path.Combine(_root, "answer.txt");
            File.WriteAllText(textPath, Guidance);
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? current = Goal;
            CliCommandDispatcher.ExecuteCommand(["attention", "answer", Goal.Id.Value, Request.Id.Value,
                "--text-file", textPath, "--evidence", "receipt-1", .. flags], Kernel, Workspace,
                ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref current);
        }

        internal async Task<OperatorIntentRecord> CliAnswer(string[] flags)
        {
            RunCli(flags);
            return (await Intents.ClaimNextAsync(Goal.Id.Value, OperatorIntentCoordinator.ClaimOwner))!;
        }

        internal void Apply()
        {
            Assert.True(Coordinator.ExecutePending(Kernel, Goal).MutatedGoalState);
            Coordinator.CompletePersisted([Goal.Id]);
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
