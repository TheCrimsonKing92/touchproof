using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: detached models, instance-owned views and injected clipboard only.
public sealed class OwnerConsoleFieldResidualTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ActivityMeaningCopiesTheDisplayedLiveActionAndNextStep(bool live)
    {
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        var goal = harness.AddGoal("11111111111111111111111111111111", "Goal", AgentRole.Developer);
        var decision = new OwnerConsoleDecision("question/full", 2, goal.Id.Value, "11111111", OwnerQuestionKind.HumanInput,
            "Continue 日本語?", "Continue 日本語?", null, null, null);
        if (live) harness.Questions.Items.Add(decision.ToQuestion());
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        controller.Apply(new(new(ConductorLiveness.NotRunning, 1, 1, 0, null, 0), [decision], [], []));
        var item = new OwnerConsoleActivityItem(At, "event", "tag", "11111111", "detail", "Goal",
            Why: "original reason", Next: "original next", Act: "original no action", Subject: decision.FullText,
            OwnerQuestionId: decision.Id, GoalId: goal.Id.Value);

        await controller.ShowActivityMeaningAsync(item, cancellationToken: TestContext.Current.CancellationToken);

        var expectedAct = live ? "Yes. Open DECISIONS row [2] 11111111 HumanInput: Continue 日本語?" :
            "No. No longer listed in DECISIONS.";
        var expectedNext = live ? "still waiting on you. Work waits for your answer." :
            "no longer listed in DECISIONS. This question no longer requires an owner answer.";
        var message = Assert.Single(dialogs.Messages);
        Assert.Contains("Owner action: " + expectedAct, message.Text);
        Assert.Contains("Next step: " + expectedNext, message.Text);
        var fields = OpenFields(message, 0);
        Assert.Equal(expectedAct, Copy(fields, "act"));
        Assert.Equal(expectedNext, Copy(fields, "next"));
        Assert.Equal(decision.FullText, Copy(fields, "why"));
        Assert.Equal(goal.Id.Value, Copy(fields, "goal"));
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public async Task AmbiguousCommandRowsCopyEachFullGoalIdAndUnfittedTitle()
    {
        var harness = new OwnerConsoleHarness();
        var first = harness.AddGoal("3333333a111111111111111111111111", "日本語 " + new string('x', 120), AgentRole.Developer);
        var second = harness.AddGoal("3333333b222222222222222222222222", "Second title", AgentRole.Tester);
        var dialogs = new Dialogs();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);

        await controller.RunCommandAsync(":goal 3333333", cancellationToken: TestContext.Current.CancellationToken);

        var message = Assert.Single(dialogs.Messages);
        Assert.Equal("Command result", message.Title);
        Assert.StartsWith("ambiguous goal", message.Text);
        foreach (var goal in new[] { first, second })
        {
            var sourceLine = Assert.Single(message.Fields.Rows!.Where(pair => pair.Value.Identity == goal.Id.Value)).Key;
            var fields = OpenFields(message, sourceLine);
            Assert.Equal(goal.Id.Value, Copy(fields, "goal"));
            Assert.Equal(OwnerGoalTitle.From(goal.Objective), Copy(fields, "title"));
        }
        Assert.Empty(harness.Answers.Calls);
    }

    [Theory]
    [InlineData(GoalPriority.Normal, GoalPriority.High, "22222222222222222222222222222222")]
    [InlineData(GoalPriority.High, GoalPriority.Critical, "22222222222222222222222222222222")]
    [InlineData(GoalPriority.Normal, GoalPriority.Critical, null)]
    public void GoalDetailCopiesEffectiveAndOwnPriorityAndFullInheritedSource(
        GoalPriority own, GoalPriority effective, string? donor)
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111111111111111111111111111", "Title", AgentRole.Developer);
        harness.Kernel.SetGoalPriority(goal.Id, own, "test", "setup");
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with {
            PriorityScheduling = new(GoalPriorities.Format(effective), InheritedFromGoalId: donor) }).ToArray() });
        goal = harness.Kernel.GetGoal(goal.Id);
        var output = new OwnerConsoleCapturedOutput();
        OwnerConsoleGoalDetailFormatter.Render(new(goal, null, []), output, TimeZoneInfo.Utc);

        Assert.Contains("Priority: " + effective, output.Text);
        var fields = OpenFields(("Goal", output.Text, output.Structured!), 0);
        Assert.Equal(effective.ToString(), Copy(fields, "priority"));
        Assert.Equal(own.ToString(), Copy(fields, "priority/own"));
        if (donor is not null) Assert.Equal(donor, Copy(fields, "priority/inherited-from"));
        else Assert.False(fields.Fields.Single(field => field.Key == "priority/inherited-from").Available);
    }

    [Fact]
    public void GoalDetailCopiesEveryLandingBlockerValueWithoutFlattening()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal("11111111111111111111111111111111", "Held", AgentRole.Developer);
        var blocker = new GoalLandingBlocker("untyped/AcceptanceQueue", "  full detail 日本語\r\nsecond line  ", "identity", At,
            "operator", "inspect gate");
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with { Goals = snapshot.Goals.Select(item => item with {
            Status = GoalStatus.Verified, LandingBlocker = blocker }).ToArray() });
        goal = harness.Kernel.GetGoal(goal.Id);
        var output = new OwnerConsoleCapturedOutput();
        OwnerConsoleGoalDetailFormatter.Render(new(goal, null, []), output, TimeZoneInfo.Utc);

        Assert.Contains("Landing blocker: ", output.Text);
        var fields = OpenFields(("Goal", output.Text, output.Structured!), 0);
        Assert.Equal(blocker.Kind, Copy(fields, "landing-blocker/kind"));
        Assert.Equal(blocker.Since.ToString("O"), Copy(fields, "landing-blocker/since"));
        Assert.Equal(blocker.NextActor, Copy(fields, "landing-blocker/next-actor"));
        Assert.Equal(blocker.NextVerb, Copy(fields, "landing-blocker/next-verb"));
        Assert.Equal(blocker.Detail, Copy(fields, "landing-blocker/detail"));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task ResolvedDecisionKeepsOriginalFieldsAndDistinctResolutionSnapshot()
    {
        var decision = new OwnerConsoleDecision("question/full", 1, "goal/full", "goal", OwnerQuestionKind.Clarification,
            "Summary", "Full question 日本語\nsecond line", "repository", "high", "proposed default");
        var resolution = new OwnerQuestionResolution(decision.Id, decision.GoalId, "Title", "Planner", "Recorded question",
            "Answered", "Author", At, "Answer λ\nsecond line", [], "Developer");
        var answers = 0;
        using var detail = new OwnerConsoleDecisionDetail(decision, TimeZoneInfo.Utc,
            (_, _, _) => { answers++; return Task.CompletedTask; }, _ => Task.FromResult<OwnerQuestionResolution?>(resolution),
            _ => Task.CompletedTask);
        detail.Observe(null);
        await detail.LastRefresh.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var state = detail.State;
        Assert.False(state.CanAnswer);
        Assert.Contains(decision.FullText, state.Text);
        Assert.Contains(resolution.Answer!, state.Text);
        var fields = OpenFields(("Decision", state.Text, new(state.Fields!)), 0);
        Assert.Equal(decision.Id, fields.Identity);
        Assert.Equal(fields.Fields.Length, fields.Fields.Select(field => field.Key).Distinct().Count());
        Assert.Equal(decision.Kind.ToString(), Copy(fields, "kind"));
        Assert.Equal(decision.FullText, Copy(fields, "question"));
        Assert.Equal(decision.BlastRadius, Copy(fields, "blast"));
        Assert.Equal(decision.Confidence, Copy(fields, "confidence"));
        Assert.Equal(decision.ProposedDefault, Copy(fields, "default"));
        Assert.Equal(resolution.Question, Copy(fields, "resolution/question"));
        Assert.Equal(resolution.Answer, Copy(fields, "resolution/answer"));
        Assert.Equal(resolution.ResolvedAt.ToString("O"), Copy(fields, "resolution/at"));
        detail.Observe(decision with { FullText = "Later question" });
        Assert.Equal(decision.FullText, Copy(fields, "question"));
        Assert.Equal(resolution.Answer, Copy(fields, "resolution/answer"));
        Assert.Equal(0, answers);
    }

    private static OwnerConsoleFieldSnapshot OpenFields(
        (string Title, string Text, OwnerConsoleStructuredText Fields) message, int sourceLine)
    {
        using var dialog = new OwnerConsoleTextDialog(message.Title, message.Text) { Structured = message.Fields };
        dialog.Format(18, 8);
        dialog.Page.Select(dialog.Page.SourceLines.ToList().FindIndex(line => line == sourceLine));
        OwnerConsoleFieldSnapshot? opened = null;
        dialog.FieldsRequested += fields => opened = fields;
        Assert.True(dialog.HandleKey(OwnerConsoleBindings.OpenFields));
        Assert.NotNull(opened);
        return opened;
    }

    private static string Copy(OwnerConsoleFieldSnapshot snapshot, string key)
    {
        var writes = new List<string>();
        using var dialog = new OwnerConsoleFieldsDialog(snapshot, new(value => { writes.Add(value); return true; }), key);
        Assert.Equal(key, dialog.SelectedField?.Key);
        Assert.True(dialog.HandleKey(OwnerConsoleBindings.CopyField));
        return Assert.Single(writes);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        internal readonly List<(string Title, string Text, OwnerConsoleStructuredText Fields)> Messages = [];
        public Task<int?> ShowStructuredAsync(string title, string text, OwnerConsoleStructuredText fields,
            IReadOnlyList<int>? choiceLines = null)
        { Messages.Add((title, text, fields)); return Task.FromResult<int?>(null); }
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Expected structured fields.");
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Copy must not act.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Copy must not answer.");
    }
}
