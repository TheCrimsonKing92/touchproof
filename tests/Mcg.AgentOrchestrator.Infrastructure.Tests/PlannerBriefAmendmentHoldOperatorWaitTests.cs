using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = PlannerBriefAmendmentAnswerRoutingTests.Fixture;

public sealed class PlannerBriefAmendmentHoldOperatorWaitTests
{
    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    public Task AuthorRecommendationPlainAnswer_IsOperatorWaitUntilDisposition(string disposition)
    {
        var question = new AuthorOwnerQuestionHold("human-input", "amendment",
            "The proposed criterion matches the fixture.", "Should the criterion be revised?",
            "Apply the proposed criterion.", OwnerQuestionReversibility.Reversible);
        var answer = AuthorRecommendationApplication.FormatAnswer(question, TimeSpan.FromMinutes(1));
        return AssertWaitUntilDisposition(answer, disposition, ["--operator-actor", "author", "--actor-kind", "Agent"],
            expectedActor: "author", expectedActorKind: OperatorActorKind.Agent);
    }

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    public Task OperatorPlainAnswer_IsOperatorWaitUntilDisposition(string disposition) =>
        AssertWaitUntilDisposition("Apply the proposed criterion.", disposition, [],
            expectedActor: "operator", expectedActorKind: OperatorActorKind.Human);

    [Theory]
    [InlineData("accept")]
    [InlineData("decline")]
    public Task FrozenFactRulingAnswer_IsOperatorWaitUntilDisposition(string disposition)
    {
        var ruling = new FrozenFactRuling(["behavior"], [new("criterion 1", "brief.md", "Preserve the existing behavior.")],
            "Preserve the regression contract.", "The implementation scope.", "Only criterion 1 changes.", ["seed.txt:1"]);
        return AssertWaitUntilDisposition(ruling.Render(), disposition, ["--operator-actor", "author", "--actor-kind", "Agent"],
            expectedActor: "author", expectedActorKind: OperatorActorKind.Agent);
    }

    [Fact]
    public async Task SharedRenderer_StatusTextLineAndJsonDetailAreIdentical()
    {
        using var f = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(f.Root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        f.Kernel.SubmitHumanInput(f.Request.Id, "Apply the proposed criterion.");
        await repository.SaveAsync(f.Kernel);
        await CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory).GetAttentionQueueAsync();

        var hold = Assert.IsType<PlannerBriefAmendmentRevisionPendingHold>(
            f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
        var expected = $"amendment-revision-pending goal={f.Goal.Id} criterion=1 request={f.Request.Id} " +
            $"requestedBriefVersion=1 currentBriefVersion=1. Amendment answered; next: {ExpectedCommands(f)}";
        Assert.Equal(expected, hold.Render());

        var snapshot = ConductorStatusSnapshotReader.Read(workspace, null, DateTimeOffset.UtcNow, mainCommit: () => null);
        using var status = new StringWriter();
        CliConductorStatusReader.Print(snapshot, status);
        Assert.Contains($"    {hold.Render()}", Lines(status.ToString()));
        Assert.Contains($"  [amendment-revision-pending] {hold.Render()}", Lines(ReadText(workspace, repository)));
        foreach (var summary in new[] { false, true })
        {
            var payload = ReadPayload(workspace, repository, summary);
            var detail = Assert.Single(payload.GetProperty("amendmentRevisionPending").EnumerateArray()).GetProperty("detail");
            Assert.Equal(hold.Render(), detail.GetString());
            var wait = Assert.Single(payload.GetProperty("waits").EnumerateArray());
            Assert.Equal(hold.NextCommands(), wait.GetProperty("nextAction").GetString());
            Assert.Equal(hold.NextCommands(), wait.GetProperty("resumeCommand").GetString());
        }
    }

    [Fact]
    public async Task AmendmentRevisionPendingSection_KeepsPropertyNamesAndShape()
    {
        using var f = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(f.Root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        f.Kernel.SubmitHumanInput(f.Request.Id, "Apply the proposed criterion.");
        await repository.SaveAsync(f.Kernel);

        foreach (var summary in new[] { false, true })
        {
            var payload = ReadPayload(workspace, repository, summary);
            var row = Assert.Single(payload.GetProperty("amendmentRevisionPending").EnumerateArray());
            Assert.Equal(new[] { "state", "hold", "detail", "identity" }, row.EnumerateObject().Select(property => property.Name));
            Assert.Equal("amendment-revision-pending", row.GetProperty("state").GetString());
            Assert.Equal(JsonValueKind.String, row.GetProperty("detail").ValueKind);
            var identity = $"amendment-revision-pending:{f.Request.Id}:1";
            Assert.Equal(identity, row.GetProperty("identity").GetString());
            var hold = row.GetProperty("hold");
            Assert.Equal(new[] { "goalId", "taskId", "requestId", "criterionIndex", "requestedBriefVersion", "currentBriefVersion", "identity" },
                hold.EnumerateObject().Select(property => property.Name));
            Assert.Equal(f.Goal.Id.Value, hold.GetProperty("goalId").GetProperty("value").GetString());
            Assert.Equal(f.Planner.Id.Value, hold.GetProperty("taskId").GetProperty("value").GetString());
            Assert.Equal(f.Request.Id.Value, hold.GetProperty("requestId").GetProperty("value").GetString());
            Assert.Equal(1, hold.GetProperty("criterionIndex").GetInt32());
            Assert.Equal(1, hold.GetProperty("requestedBriefVersion").GetInt32());
            Assert.Equal(1, hold.GetProperty("currentBriefVersion").GetInt32());
            Assert.Equal(identity, hold.GetProperty("identity").GetString());
        }
    }

    private static async Task AssertWaitUntilDisposition(string answer, string disposition, string[] attribution,
        string expectedActor, OperatorActorKind expectedActorKind)
    {
        using var f = new Fixture(objective: "Implement the current brief.\n\n## Acceptance criteria\n" +
            "- Reproduce the regression.\n");
        var workspace = CreateRefinedWorkspace(f.Root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var coordinator = OperatorIntentCoordinator.CreateDefault(workspace);
        var notePath = Path.Combine(f.Root, "answer.txt");
        await File.WriteAllTextAsync(notePath, answer);
        PlannerBriefAmendmentAnswerRoutingTests.ExecuteAttentionAnswer(f, workspace, ["--text-file", notePath, .. attribution]);
        await ApplyAnswerAsync(coordinator, f, repository);
        var appliedAnswer = Assert.Single(f.Goal.Timeline.Select(entry => entry.OperatorIntentApplied)
            .OfType<OperatorIntentAppliedPayload>(), applied => applied.Verb == OperatorIntentVerbs.Answer);
        Assert.Equal(expectedActor, appliedAnswer.Actor);
        Assert.Equal(expectedActorKind, appliedAnswer.ActorKind);
        Assert.Equal(answer, f.Request.Answer);
        Assert.Null(f.Request.AuthoritativeAnswer!.AmendmentDisposition);

        var commands = ExpectedCommands(f);
        var requestedAt = f.Request.RequestedAt.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        foreach (var summary in new[] { false, true })
        {
            var payload = ReadPayload(workspace, repository, summary);
            var wait = Assert.Single(payload.GetProperty("waits").EnumerateArray());
            Assert.Equal(f.Request.Id.Value, wait.GetProperty("id").GetString());
            Assert.Equal(f.Goal.Id.Value, wait.GetProperty("goalId").GetString());
            Assert.Equal(f.Planner.Id.Value, wait.GetProperty("taskId").GetString());
            Assert.Equal("PlannerBriefAmendment", wait.GetProperty("kind").GetString());
            Assert.Equal("amendment-revision-pending", wait.GetProperty("state").GetString());
            Assert.Equal(1, wait.GetProperty("criterionIndex").GetInt32());
            Assert.Equal("operator", wait.GetProperty("owner").GetString());
            Assert.Equal(requestedAt, wait.GetProperty("requestedAt").GetString());
            Assert.Equal(commands, wait.GetProperty("nextAction").GetString());
            Assert.Equal(commands, wait.GetProperty("resumeCommand").GetString());
        }

        var text = Lines(ReadText(workspace, repository));
        Assert.Contains("Operator waits: 1 amendment-revision-pending wait(s)", text);
        Assert.Equal($"  [PlannerBriefAmendment] {f.Request.Id} goal={f.Goal.Id} task={f.Planner.Id} " +
            $"criterion=1 owner=operator requestedAt={requestedAt}",
            Assert.Single(text, line => line.StartsWith("  [PlannerBriefAmendment] ", StringComparison.Ordinal)));
        Assert.Contains($"    resume: {commands}", text);

        await File.WriteAllTextAsync(notePath, $"Record operator disposition {disposition}.");
        PlannerBriefAmendmentAnswerRoutingTests.ExecuteAttentionAnswer(f, workspace, ["--text-file", notePath, "--amendment", disposition]);
        await ApplyAnswerAsync(coordinator, f, repository);

        foreach (var summary in new[] { false, true })
        {
            var payload = ReadPayload(workspace, repository, summary);
            Assert.Empty(payload.GetProperty("waits").EnumerateArray());
            Assert.False(payload.TryGetProperty("amendmentRevisionPending", out _));
        }
        Assert.DoesNotContain(f.Request.Id.Value, ReadText(workspace, repository));
    }

    private static async Task ApplyAnswerAsync(OperatorIntentCoordinator coordinator, Fixture f,
        SqliteOrchestratorStateRepository repository)
    {
        Assert.True(coordinator.ExecutePending(f.Kernel, f.Goal).MutatedGoalState);
        await repository.SaveAsync(f.Kernel);
        coordinator.CompletePersisted([f.Goal.Id]);
    }

    private static JsonElement ReadPayload(OrchestratorWorkspace workspace, IOrchestratorStateQueries queries, bool summary)
    {
        string[] args = summary ? ["attention", "list", "--summary", "--json"] : ["attention", "--json"];
        using var json = JsonDocument.Parse(CaptureConsole(() => CliAttentionJsonQueryCommand.Execute(args, queries, workspace)));
        return json.RootElement.GetProperty("payload").Clone();
    }

    private static string ReadText(OrchestratorWorkspace workspace, IOrchestratorStateQueries queries) =>
        CaptureConsole(() =>
        {
            IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
            var profiles = WorkerProfileCatalog.Default();
            Goal? currentGoal = null;
            Assert.True(CliAttentionQueryCommand.TryExecute(["attention", "list"], queries, workspace,
                new InMemoryModelProviderRegistry([]), ref agents, ref profiles, ref currentGoal));
        });

    private static string[] Lines(string text) => text.ReplaceLineEndings("\n").Split('\n');

    private static string ExpectedCommands(Fixture f)
    {
        var goal = f.Goal.Id.Value[..8];
        var request = f.Request.Id.Value[..8];
        return $"attention answer {goal} {request} --text-file <path> --amendment accept | " +
            $"attention answer {goal} {request} --text-file <path> --amendment decline | " +
            $"revise {goal} --brief-file <path> --reason <reason>";
    }
}
