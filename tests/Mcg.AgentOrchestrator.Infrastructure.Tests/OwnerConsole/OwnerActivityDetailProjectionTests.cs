using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;

// Parallel-safe: instance-owned controller, detached stores and dialogs; no real clipboard.
public sealed class OwnerActivityDetailProjectionTests
{
    [Fact]
    public async Task RejectionReachesControllerDialogWithDistinctRowsAndExactCopyValues()
    {
        var harness = new OwnerConsoleHarness();
        var dialogs = new Capture();
        using var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, dialogs,
            harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
        var item = OwnerActivityDetailFixture.Failure();
        await controller.ShowActivityMeaningAsync(item, cancellationToken: TestContext.Current.CancellationToken);
        using var dialog = Assert.IsType<OwnerConsoleTextDialog>(dialogs.Dialog);
        Assert.StartsWith("What happened: Developer completion was rejected.\n\nReason: verification-pattern-unmatched\n", dialogs.Text);
        Assert.Contains("\n\nNext step: The next step was not recorded", dialogs.Text);
        Assert.Contains("\n\nOwner action: ", dialogs.Text);
        Assert.Contains("\n\nDiagnostics\n", dialogs.Text);
        Assert.Contains("post_dispatch_commits: 0\nchanged_paths: none\ntests_status: Deferred\nblockers: None\n", dialogs.Text);
        Assert.Equal(1, dialogs.Text.Split("matched no accepted verification pattern", StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain("retry", dialogs.Text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("code is wrong", dialogs.Text);
        var fields = dialog.Structured!.Detail!;
        Assert.Equal(item.GoalId, fields.Fields.Single(field => field.Key == "goal").Value);
        Assert.Equal(OwnerActivityDiagnosticParserTests.Raw(), fields.Fields.Single(field => field.Key == "event/0/original").Value);
        var copies = new List<string>();
        using var modal = new OwnerConsoleFieldsDialog(fields, new(value => { copies.Add(value); return true; }), "event/0/diagnostic/worktree/0");
        modal.HandleKey(OwnerConsoleBindings.CopyField);
        Assert.Equal(fields.Fields.Single(field => field.Key == "event/0/diagnostic/worktree/0").Value, Assert.Single(copies));
        Assert.Empty(harness.Answers.Calls);
    }

    [Fact]
    public void UnknownAndTypedValuesKeepExactSourceWithoutGuessingProseBoundaries()
    {
        const string raw = "unknown a=two words empty= none_\r\n  \"quoted=x\"  ";
        var source = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "unknown", "full-goal", "structural tokens",
            OwnerActivityDetailEvidence.From(raw, new("opaque", "Opaque", "  none_\r\n=  "), new("empty", "Empty", "")));
        var item = new OwnerConsoleActivityItem(source.Timestamp, source.EventKind, "tag", "full-goa", source.Detail,
            Evidence: [source]);
        var detail = OwnerActivityExplanation.ExplainWithFields(item);
        Assert.Contains("\n\nOriginal event\nSource: unknown a=two words empty= none_\n    ", detail.Text);
        Assert.Equal(raw, detail.Fields.Fields.Single(field => field.Key == "event/0/original").Value);
        Assert.Equal("  none_\r\n=  ", detail.Fields.Fields.Single(field => field.Key == "event/0/opaque").Value);
        Assert.DoesNotContain(detail.Fields.Fields, field => field.Key == "event/0/empty");
        Assert.DoesNotContain(detail.Fields.Fields, field => field.Key.Contains("diagnostic/"));
    }

    [Fact]
    public void GroupedLandingsKeepEachEventAndNormalCompletionKeepsFullMessage()
    {
        var at = DateTimeOffset.UnixEpoch;
        OwnerConductEvent[] sources = [new(at, "loop-relaunch", "first/full", "LOOP_RELAUNCH_SCHEDULED tick=1"),
            new(at, "loop-relaunch", "second/full", "LOOP_RELAUNCH_NOT_REQUIRED tick=1")];
        var grouped = Assert.Single(OwnerActivityNarrator.Narrate(sources, id => id!));
        var detail = OwnerActivityExplanation.ExplainWithFields(grouped);
        Assert.Equal(sources, grouped.Evidence);
        foreach (var i in Enumerable.Range(0, 2))
        {
            Assert.Equal(sources[i].GoalId, detail.Fields.Fields.Single(field => field.Key == $"event/{i}/goal").Value);
            Assert.Equal(sources[i].Detail, detail.Fields.Fields.Single(field => field.Key == $"event/{i}/original").Value);
        }
        var line = JsonSerializer.Serialize(new { occurredAt = at, eventType = "TaskCompleted", taskId = "full-task", role = "Developer", message = "done none_ = \"full\"\r\nnext" });
        Assert.True(OwnerGoalLifecycleEvent.TryParse(line, "full-goal", out var completed));
        var normal = OwnerActivityExplanation.ExplainWithFields(Assert.Single(OwnerActivityNarrator.Narrate([completed!], _ => "Title")));
        Assert.Contains("Developer passed Title", normal.Text);
        Assert.Equal("done none_ = \"full\"\r\nnext", normal.Fields.Fields.Single(field => field.Key == "event/0/original").Value);
    }

    [Theory]
    [InlineData("q", "11111111", "Continue?", true)]
    [InlineData("other", "11111111", "Continue?", false)]
    [InlineData("q", "22222222", "Continue?", false)]
    [InlineData("q", "11111111", "Different question?", false)]
    public void OwnerActionRequiresExactQuestionGoalAndSubject(string id, string goal, string subject, bool matches)
    {
        var question = new OwnerQuestion("q", "11111111", OwnerQuestionKind.HumanInput, "Continue?");
        var item = Assert.Single(OwnerActivityNarrator.Narrate([], _ => "Title", attention: [new(question, DateTimeOffset.UnixEpoch)]));
        var decision = new OwnerConsoleDecision(id, 1, goal, goal, OwnerQuestionKind.HumanInput, subject, subject, null, null, null);
        var detail = OwnerActivityExplanation.ExplainWithFields(item, [decision]);
        Assert.Equal(matches, detail.Fields.Fields.Single(field => field.Key == "act").Value!.StartsWith("Yes. Open DECISIONS"));
    }

    [Fact]
    public void SharedGoalPrefixAndReparsedEventsDoNotForgeCorrelationOrDuplicates()
    {
        const string first = "11111111aaaaaaaaaaaaaaaaaaaaaaaa";
        const string second = "11111111bbbbbbbbbbbbbbbbbbbbbbbb";
        var question = new OwnerQuestion("q", first, OwnerQuestionKind.HumanInput, "Continue?");
        var item = Assert.Single(OwnerActivityNarrator.Narrate([], _ => "Title", attention: [new(question, DateTimeOffset.UnixEpoch)]));
        var unrelated = new OwnerConsoleDecision("q", 1, second, "11111111", OwnerQuestionKind.HumanInput,
            "Continue?", "Continue?", null, null, null);
        var detail = OwnerActivityExplanation.ExplainWithFields(item, [unrelated]);
        Assert.DoesNotContain("Yes. Open DECISIONS", detail.Text);
        using var fixture = OwnerActivityDetailFixture.Read();
        var record = fixture.RootElement.GetProperty("task_failure");
        Assert.Single(OwnerActivityNarrator.Narrate([OwnerActivityDetailFixture.Event(record), OwnerActivityDetailFixture.Event(record)], _ => "Title"));
    }

    [Fact]
    public void ConflictingRepeatedReasonsAndRolesRemainSeparateWithoutSelectingACause()
    {
        var raw = OwnerActivityDiagnosticParserTests.Raw() + " reason=other-cause role=Reviewer";
        var source = new OwnerConductEvent(DateTimeOffset.UnixEpoch, "goal-escalation", "goal", raw);
        var item = Assert.Single(OwnerActivityNarrator.Narrate([source], _ => "Title"));
        var detail = OwnerActivityExplanation.ExplainWithFields(item);
        Assert.StartsWith("What happened: Worker completion was rejected.\n\nReason: Multiple rejection reasons were recorded", detail.Text);
        Assert.Equal("verification-pattern-unmatched", detail.Fields.Fields.Single(field => field.Key == "event/0/diagnostic/reason/0").Value);
        Assert.Equal("other-cause", detail.Fields.Fields.Single(field => field.Key == "event/0/diagnostic/reason/1").Value);
    }

    [Fact]
    public void TypedRoleTakesPrecedenceAndConflictingTextRetainsItsOwnProvenance()
    {
        var raw = OwnerActivityDiagnosticParserTests.Raw().Replace("role=Developer", "role=Reviewer", StringComparison.Ordinal);
        var line = JsonSerializer.Serialize(new { occurredAt = DateTimeOffset.UnixEpoch, eventType = "TaskFailed", role = "Developer", taskId = "typed-task", message = raw });
        Assert.True(OwnerGoalLifecycleEvent.TryParse(line, "full-goal", out var source));
        var detail = OwnerActivityExplanation.ExplainWithFields(Assert.Single(OwnerActivityNarrator.Narrate([source!], _ => "Title")));
        Assert.StartsWith("What happened: Developer completion was rejected.", detail.Text);
        Assert.Equal("Developer", detail.Fields.Fields.Single(field => field.Key == "event/0/recorded/role").Value);
        Assert.Equal("Reviewer", detail.Fields.Fields.Single(field => field.Key == "event/0/diagnostic/role/0").Value);
        Assert.Equal("typed-task", detail.Fields.Fields.Single(field => field.Key == "event/0/recorded/taskId").Value);
        Assert.Equal(raw, detail.Fields.Fields.Single(field => field.Key == "event/0/original").Value);
    }

    internal sealed class Capture : IOwnerConsoleDialogs
    {
        internal OwnerConsoleTextDialog? Dialog;
        internal string Text = "";
        public Task<int?> ShowStructuredAsync(string title, string text, OwnerConsoleStructuredText fields, IReadOnlyList<int>? choiceLines = null)
        { Text = text; Dialog = new(title, text, choiceLines) { Structured = fields }; return Task.FromResult<int?>(null); }
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Structured Activity path required.");
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("No action expected.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("No prompt expected.");
    }
}
