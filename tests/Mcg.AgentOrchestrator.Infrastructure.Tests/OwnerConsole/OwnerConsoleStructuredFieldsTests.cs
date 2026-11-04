using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Input;

// Parallel-safe: production views over detached typed fixtures and injected clipboard sinks.
public sealed class OwnerConsoleStructuredFieldsTests
{
    [Fact]
    public void LandingsWrappedRowsExposeFullValuesAndEnterRemainsSeparate()
    {
        var row = new OwnerLandingsQuery.Row(OwnerConsoleFieldCopyScene.FirstId, "  長い title\nwith detail  ", DateTimeOffset.UnixEpoch,
            "123456789", "solo", "passed", "Epic") { FullRevision = new string('a', 40) };
        var page = OwnerLandingsPage.From(new(false, "today", [row], 0));
        using var dialog = new OwnerConsoleTextDialog("Landings", page.Text, page.ChoiceLines) { Structured = page.Fields };
        dialog.Format(20, 8);
        var opened = new List<OwnerConsoleFieldSnapshot>();
        var choices = new List<int>();
        dialog.FieldsRequested += opened.Add; dialog.ChoiceAccepted += choices.Add;
        var line = dialog.Page.SourceLines.ToList().FindIndex(source => source == page.ChoiceLines[0]);
        dialog.Page.Select(line); dialog.HandleKey(OwnerConsoleBindings.OpenFields);
        var fields = Assert.Single(opened);
        var writes = new List<string>();
        using var modal = new OwnerConsoleFieldsDialog(fields, new(value => { writes.Add(value); return true; }), "revision");
        modal.HandleKey(OwnerConsoleBindings.CopyField);
        modal.Fields.SelectedItem = fields.Fields.ToList().FindIndex(field => field.Key == "title");
        modal.Copy();
        Assert.Equal([new string('a', 40), row.Title], writes);
        Assert.Empty(choices);
        dialog.HandleKey(Key.Enter);
        Assert.Equal(0, Assert.Single(choices));
    }

    [Fact]
    public async Task DecisionAndActivityF2TargetDisplayedTypedRowsIncludingRetainedContent()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        var decision = new OwnerConsoleDecision("question-full", 1, OwnerConsoleFieldCopyScene.FirstId, "11111111", OwnerQuestionKind.HumanInput,
            "Summary", "Unicode λ\nfull question", null, "high", "");
        var activity = new OwnerConsoleActivityItem(DateTimeOffset.UnixEpoch, "event", "tag", "11111111", "full activity detail",
            GoalId: OwnerConsoleFieldCopyScene.FirstId);
        var model = scene.Model with { Decisions = [decision], Activity = [activity] };
        scene.View.Render(model); scene.View.FocusDecisions();
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.OpenFields);
        Assert.Equal("question-full", scene.Dialog.Fields!.Snapshot.Identity);
        Assert.Equal(decision.FullText, scene.Dialog.Fields.Snapshot.Fields.Single(field => field.Key == "question").Value);
        scene.View.Render(model with { Decisions = [], DecisionsState = new(Error: "failed") });
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.OpenFields);
        Assert.Equal("question-full", scene.Dialog.Fields!.Snapshot.Identity);
        await scene.View.HandleKeyAsync(Key.Tab); await scene.View.HandleKeyAsync(Key.Tab);
        scene.View.ActivityPane.SelectedItem = 0;
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.OpenFields);
        Assert.Equal(activity.Detail, scene.Dialog.Fields!.Snapshot.Fields.Single(field => field.Key == "detail").Value);
        Assert.Equal(0, scene.Dialog.TextOpens); Assert.Empty(scene.Harness.Answers.Calls);
    }

    [Fact]
    public async Task GateF2UsesSelectedLaneBeforeFitting()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        var lane = new OwnerConsoleGateLane("long lane 日本語", OwnerConsoleGateLaneState.Running, "running", "remote executor",
            TimeSpan.FromSeconds(12), null, false, Target: "some/project/path/with/full/value", HeartbeatPath: "C:\\work\\heartbeat.json");
        var gate = new OwnerConsoleGateProgress(true, "solo", [new(OwnerConsoleFieldCopyScene.FirstId, "Goal")],
            DateTimeOffset.UnixEpoch, TimeSpan.Zero, [lane], 0, 0, 0, 1);
        scene.View.Render(scene.Model with { Gate = gate });
        await scene.View.HandleKeyAsync(new Key('g'));
        var pane = scene.View.GateView.BodyPane;
        var map = new Dictionary<int, OwnerConsoleFieldSnapshot>();
        OwnerConsoleGateLayout.Lines(gate, 100, false, map);
        pane.SelectedItem = map.Single(pair => pair.Value.Identity == "lane/" + lane.Name).Key;
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.OpenFields);
        var modal = scene.Dialog.Fields!;
        Assert.Equal("lane/" + lane.Name, modal.Snapshot.Identity);
        modal.Fields.SelectedItem = modal.Snapshot.Fields.ToList().FindIndex(field => field.Key == "target"); modal.Copy();
        Assert.Equal(lane.Target, Assert.Single(scene.Copies));
        Assert.True(scene.View.GateView.IsOpen); Assert.Equal(0, scene.Dialog.TextOpens);
    }

    [Fact]
    public void GoalCreateViewExposesTypedSnapshotThroughSharedDialog()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        var goal = scene.Harness.Kernel.GetGoal(new GoalId(OwnerConsoleFieldCopyScene.FirstId));
        var output = new OwnerConsoleCapturedOutput();
        OwnerConsoleGoalDetailFormatter.Render(new(goal!, null, []), output, TimeZoneInfo.Utc);
        using var goalDialog = new OwnerConsoleGoalDialog(scene.Controller, new(output.Text, [], [], null, null, null, Fields: output.Structured),
            _ => Task.FromResult<OwnerConsoleGoalDialog.Content?>(null), _ => Task.CompletedTask, (_, _) => Task.CompletedTask, default);
        var built = goalDialog.CreateView(() => { }, _ => Task.CompletedTask);
        using var view = built.Dialog;
        OwnerConsoleFieldSnapshot? captured = null;
        view.FieldsRequested += fields => captured = fields;
        view.HandleKey(OwnerConsoleBindings.OpenFields);
        Assert.NotNull(captured);
        using var modal = new OwnerConsoleFieldsDialog(captured, scene.Dialog.Clipboard, "goal");
        modal.Copy();
        Assert.Equal(OwnerConsoleFieldCopyScene.FirstId, Assert.Single(scene.Copies));
        Assert.Contains(captured.Fields, field => field.Key.EndsWith("/status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EpicListAndMemberUseSameFieldsInputRoute()
    {
        var source = new Epics();
        using var scene = new OwnerConsoleFieldCopyScene(source);
        source.Goal = scene.Harness.Kernel.GetGoal(new GoalId(OwnerConsoleFieldCopyScene.FirstId));
        await scene.View.HandleKeyAsync(new Key('e'));
        Assert.True(scene.View.EpicView.IsOpen);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.OpenFields);
        Assert.Equal("epic-full-identity", scene.Dialog.Fields!.Snapshot.Identity);
        Assert.Equal(source.Row.Epic.Description, scene.Dialog.Fields.Snapshot.Fields.Single(field => field.Key == "description").Value);
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.True(scene.View.EpicView.ShowingDetail);
        var detail = OwnerConsoleEpicViewModel.BuildDetail(source.Row, null, new Dictionary<string, Goal> { [source.Goal.Id.Value] = source.Goal });
        var map = new Dictionary<int, OwnerConsoleFieldSnapshot>();
        OwnerConsoleEpicFormatter.DetailLines(detail, 100, map);
        scene.View.EpicView.BodyPane.SelectedItem = map.First(pair => pair.Value.Identity == source.Goal.Id.Value).Key;
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.OpenFields);
        var modal = scene.Dialog.Fields!;
        Assert.Equal(source.Goal.Id.Value, modal.Snapshot.Identity);
        modal.Fields.SelectedItem = modal.Snapshot.Fields.ToList().FindIndex(field => field.Key == "title"); modal.Copy();
        Assert.Equal("  Member 日本語 full title  ", Assert.Single(scene.Copies));
        Assert.Equal(0, scene.Dialog.TextOpens); Assert.Empty(scene.Harness.Answers.Calls);
    }

    private sealed class Epics : IOwnerConsoleEpicSource
    {
        internal Goal Goal = null!;
        internal EpicProgressRollup Row { get; } = new(new("epic-full-identity", "Epic title", null,
            DateTimeOffset.UnixEpoch, "test", DateTimeOffset.UnixEpoch, "test", "Full description\nnext line"), null, [],
            [new(OwnerConsoleFieldCopyScene.FirstId, EpicProgressBucket.Active, "Active", DateTimeOffset.UnixEpoch, "  Member 日本語 full title  ")],
            1, 0, 1, 0, 0, 0, DateTimeOffset.UnixEpoch, 0, 0, 0, 0, 0, 0);
        public Task<IReadOnlyList<EpicProgressRollup>> LoadAsync(DateTimeOffset? since, CancellationToken token) => Task.FromResult<IReadOnlyList<EpicProgressRollup>>([Row]);
        public Task<EpicDetailReadSnapshot> LoadDetailAsync(string id, DateTimeOffset? since, CancellationToken token) =>
            Task.FromResult(new EpicDetailReadSnapshot(Row, since, new Dictionary<string, Goal> { [Goal.Id.Value] = Goal }));
        public Task<string?> FindGoalEpicAsync(string id, CancellationToken token) => Task.FromResult<string?>(Row.Epic.Id);
    }
}
