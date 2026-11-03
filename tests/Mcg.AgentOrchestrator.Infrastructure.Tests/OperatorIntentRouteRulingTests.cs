using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each case owns its kernel, files and temporary SQLite stores.
public sealed class OperatorIntentRouteRulingTests
{
    private const string Ruling = """
        Frozen-fact ruling v1
        Frozen classes: Ns.SomeTests
        Amended fact: Ns.SomeTests.Bound in tests/SomeTests.cs: Change the upper bound from 45 to 46.
        Basis: The measured contract now includes one more item.
        Unmodified: All other assertions and tests remain unchanged.
        Diff check: Only the bound literal in SomeTests.Bound may change.
        Evidence: receipt-1
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ruling_file_routes_and_reaches_developer_and_reviewer(bool surroundingWhitespace)
    {
        using var fixture = new Fixture();
        var text = surroundingWhitespace ? " \r\n" + Ruling.ReplaceLineEndings("\r\n") + "\r\n " : Ruling;
        var payload = await fixture.PreparePayload(text);
        Assert.Equal(text, payload.RulingText);
        var intent = await fixture.Enqueue(payload);

        Assert.True(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);
        fixture.Coordinator.CompletePersisted([fixture.Goal.Id]);

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        Assert.Equal(RetryCause.NewTestFinding, fixture.Task.PendingRetryCause);
        Assert.Equal(payload.Text, fixture.Task.AcceptedRetryFeedback?.Message);
        var request = Assert.Single(fixture.Kernel.HumanInputRequests);
        Assert.Equal(fixture.Goal.Id, request.GoalId);
        Assert.Equal(HumanWaitKind.SpecClarification, request.Kind);
        Assert.True(request.IsCompleted);
        Assert.Equal(HumanInputAnswerOrigin.Operator, request.AuthoritativeAnswer!.Origin);
        Assert.Equal(text, request.Answer);
        Assert.Empty(fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id));
        var brief = fixture.Kernel.BuildTaskBrief(fixture.Goal.Id, fixture.Task.Id).Content;
        Assert.Contains("## Frozen-fact rulings", brief);
        Assert.Contains($"Request {request.Id.Value}:", brief);
        Assert.Contains("SomeTests.Bound", brief);
        var classified = Assert.Single(ReviewerChangedExistingTestClassifier.Classify(
            [new ChangedExistingTest("SomeTests", "Bound", "tests/SomeTests.cs", 1, 3, false)],
            fixture.Kernel.HumanInputRequests, fixture.Goal.Id));
        Assert.Equal(ChangedExistingTestClass.AllowedByRuling, classified.Class);
        Assert.Equal(request.Id.Value, classified.RulingRequestId);

        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot());
        Assert.Equal(text, Assert.Single(restored.HumanInputRequests).Answer);
    }

    [Fact]
    public async Task Malformed_ruling_is_rejected_before_retry_or_recording()
    {
        using var fixture = new Fixture();
        var malformed = string.Join("\n", Ruling.ReplaceLineEndings("\n").Split('\n')
            .Where(line => !line.StartsWith("Diff check:", StringComparison.Ordinal)));
        var intent = await fixture.Enqueue(await fixture.PreparePayload(malformed));

        Assert.False(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);

        await fixture.AssertRejected(intent, "frozen-fact-ruling-malformed");
    }

    [Theory]
    [InlineData("close")]
    [InlineData("reopen-regate")]
    public async Task Non_route_intent_rejects_ruling_before_other_validation(string shape)
    {
        using var fixture = new Fixture();
        var intent = await fixture.Enqueue(fixture.Payload("malformed") with { Shape = shape });

        Assert.False(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);

        await fixture.AssertRejected(intent, "ruling-requires-route");
    }

    [Theory]
    [InlineData("close")]
    [InlineData("reopen-regate")]
    public void Non_route_cli_rejects_ruling_before_reading_files(string shape)
    {
        using var fixture = new Fixture();
        var missing = Path.Combine(fixture.Root, "missing.md");
        var parts = new[] { "adjudicate", "1", shape, "--text-file", missing,
            "--evidence", "receipt-1", "--ruling-text-file", missing };

        var error = Assert.Throws<ArgumentException>(() =>
            CliCommandHandlers.PrepareGoalScopedTaskMutationCommand(
                new CliArgumentParser.GoalScopedTaskTargetArgs(parts, null), false, fixture.Workspace));

        Assert.StartsWith("ruling-requires-route", error.Message);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
    }

    [Fact]
    public void Missing_ruling_file_leaves_route_unapplied()
    {
        using var fixture = new Fixture();
        var missing = Path.Combine(fixture.Root, "missing.md");
        var parts = new[] { "adjudicate", "1", "route", "--text-file", missing,
            "--evidence", "receipt-1", "--cause", "NewTestFinding", "--ruling-text-file", missing };

        var error = Assert.Throws<FileNotFoundException>(() =>
            CliCommandHandlers.PrepareGoalScopedTaskMutationCommand(
                new CliArgumentParser.GoalScopedTaskTargetArgs(parts, null), false, fixture.Workspace));

        Assert.Equal(missing, error.FileName);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
        Assert.Equal(WorkTaskStatus.Failed, fixture.Task.Status);
    }

    [Fact]
    public async Task Replayed_intent_after_checkpoint_keeps_one_ruling_and_retry()
    {
        using var fixture = new Fixture();
        var intent = await fixture.Enqueue(fixture.Payload(Ruling));
        Assert.True(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);
        Assert.Equal(OperatorIntentStatus.Claimed, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        var original = Assert.Single(fixture.Kernel.HumanInputRequests);
        var restored = AgentOrchestratorKernel.FromSnapshot(fixture.Kernel.ExportSnapshot());
        var goal = restored.GetGoal(fixture.Goal.Id);
        var coordinator = fixture.NewCoordinator();

        var replay = coordinator.ExecutePending(restored, goal);
        coordinator.CompletePersisted([goal.Id]);

        Assert.False(replay.MutatedGoalState);
        Assert.Equal(original.Id, Assert.Single(restored.HumanInputRequests).Id);
        Assert.Single(goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
    }

    [Fact]
    public async Task Applying_same_intent_directly_records_one_ruling()
    {
        using var fixture = new Fixture();
        var payload = fixture.Payload(Ruling);
        var intent = await fixture.Enqueue(payload);
        var adjudication = new OperatorIntentAdjudication(fixture.Decisions, _ => Fixture.Version,
            new AdjudicationEvidenceResolver());

        adjudication.Apply(fixture.Kernel, fixture.Goal, fixture.Task, intent, payload, intent.CreatedAt);
        adjudication.Apply(fixture.Kernel, fixture.Goal, fixture.Task, intent, payload, intent.CreatedAt);

        Assert.Equal(Ruling, Assert.Single(fixture.Kernel.HumanInputRequests).Answer);
    }

    [Fact]
    public async Task Separate_intents_with_identical_text_record_separate_rulings()
    {
        using var fixture = new Fixture();
        await fixture.Enqueue(fixture.Payload(Ruling));
        Assert.True(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);
        fixture.Coordinator.CompletePersisted([fixture.Goal.Id]);
        await fixture.Enqueue(fixture.Payload(Ruling));

        Assert.True(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);

        Assert.Equal(2, fixture.Kernel.HumanInputRequests.Count);
        Assert.All(fixture.Kernel.HumanInputRequests, request => Assert.Equal(Ruling, request.Answer));
    }

    [Fact]
    public async Task Failed_route_precondition_records_no_ruling()
    {
        using var fixture = new Fixture();
        var intent = await fixture.Enqueue(fixture.Payload(Ruling) with
        {
            Precondition = AdjudicationPrecondition.Capture(fixture.Goal, fixture.Task) with
            {
                TaskStatus = nameof(WorkTaskStatus.Completed)
            }
        });

        Assert.False(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);

        await fixture.AssertRejected(intent, "stale-goal-state-version");
    }

    [Fact]
    public async Task Route_without_flag_keeps_retry_behavior_and_records_no_request()
    {
        using var fixture = new Fixture();
        var payload = await fixture.PreparePayload(null);
        Assert.Null(payload.RulingText);
        var intent = await fixture.Enqueue(payload);

        Assert.True(fixture.Coordinator.ExecutePending(fixture.Kernel, fixture.Goal).MutatedGoalState);
        fixture.Coordinator.CompletePersisted([fixture.Goal.Id]);

        Assert.Equal(OperatorIntentStatus.Applied, (await fixture.Intents.GetAsync(intent.Id))!.Status);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        Assert.Equal(RetryCause.NewTestFinding, fixture.Task.PendingRetryCause);
        Assert.Equal(payload.Text, fixture.Task.AcceptedRetryFeedback?.Message);
        Assert.Empty(fixture.Kernel.HumanInputRequests);
    }

    [Fact]
    public void Recorder_existing_callers_keep_other_kind_and_normalization()
    {
        using var fixture = new Fixture();

        var request = fixture.Kernel.RecordAnsweredOperatorHumanInput(fixture.Goal.Id, "Operator evidence", " answer ");

        Assert.Equal(HumanWaitKind.Other, request.Kind);
        Assert.True(request.IsCompleted);
        Assert.Equal("answer", request.Answer);
    }

    private sealed class Fixture : IDisposable
    {
        internal const long Version = 7;
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"route-ruling-{Guid.NewGuid():N}");
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal TaskSpec Task { get; } = new(TaskId.New(), "Apply the bounded correction", AgentRole.Developer);
        internal Goal Goal { get; }
        internal OrchestratorWorkspace Workspace { get; }
        internal SqliteOperatorIntentStore Intents { get; }
        internal CollaborationItemStore Decisions { get; }
        internal OperatorIntentCoordinator Coordinator { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Workspace = OrchestratorWorkspace.ForDirectory(Root);
            Goal = Kernel.CreateGoal("Route a frozen-fact ruling", [Task]);
            Kernel.ActivateGoal(Goal.Id, AgentCatalog.Default().Agents);
            Kernel.ReportTaskProgress(Goal.Id, Task.Id, WorkTaskStatus.Failed, "The bound needs correction.");
            Intents = new SqliteOperatorIntentStore(Path.Combine(Root, "operator-intents.db"), Path.Combine(Root, "logs"));
            Decisions = CollaborationItemStore.ForDirectory(Root);
            Coordinator = NewCoordinator();
        }

        internal OperatorIntentCoordinator NewCoordinator() =>
            new(Intents, decisions: Decisions, goalStateVersionResolver: _ => Version);

        internal AdjudicateOperatorIntentPayload Payload(string? ruling) =>
            new("route", "Apply the bounded correction.", ["receipt-1"], Version, Root,
                Cause: nameof(RetryCause.NewTestFinding), RulingText: ruling);

        internal async Task<AdjudicateOperatorIntentPayload> PreparePayload(string? ruling)
        {
            Directory.CreateDirectory(Workspace.OrchestratorDirectory);
            StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
            await new SqliteOrchestratorStateRepository(Workspace.SqliteStatePath)
                .SaveAsync(Kernel);
            var note = Path.Combine(Root, "note.md");
            File.WriteAllText(note, "Apply the bounded correction.");
            var parts = new List<string> { "adjudicate", "1", "route", "--text-file", note,
                "--evidence", "receipt-1", "--cause", "NewTestFinding" };
            var path = Path.Combine(Root, "ruling.md");
            if (ruling is not null)
            {
                File.WriteAllText(path, ruling, System.Text.Encoding.UTF8);
                parts.AddRange(["--ruling-text-file", path]);
            }
            CliCommandHelp.ThrowIfInvalidFlags(parts);
            var command = CliCommandHandlers.PrepareGoalScopedTaskMutationCommand(
                new CliArgumentParser.GoalScopedTaskTargetArgs(parts, null), false, Workspace);
            if (ruling is not null) File.Delete(path);
            return CliCommandHandlers.BuildAdjudicationPayload(command, Workspace, Goal, Task);
        }

        internal Task<OperatorIntentRecord> Enqueue(AdjudicateOperatorIntentPayload payload)
        {
            var id = Guid.NewGuid().ToString("N");
            return Intents.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.Adjudicate,
                Goal.Id.Value, Task.Id.Value, JsonSerializer.Serialize(payload, OperatorIntentJson.Options),
                [], "operator", "cli", "local-process", DateTimeOffset.UtcNow));
        }

        internal async Task AssertRejected(OperatorIntentRecord intent, string reason)
        {
            var rejected = await Intents.GetAsync(intent.Id);
            Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
            Assert.Contains(reason, rejected.Outcome, StringComparison.Ordinal);
            Assert.Equal(WorkTaskStatus.Failed, Task.Status);
            Assert.Equal(RetryCause.Unknown, Task.PendingRetryCause);
            Assert.Null(Task.LatestRetryAt);
            Assert.Empty(Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
            Assert.Empty(Kernel.HumanInputRequests);
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
