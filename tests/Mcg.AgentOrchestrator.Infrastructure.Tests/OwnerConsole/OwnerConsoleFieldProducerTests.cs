using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.App.Cli;

// Parallel-safe: producer snapshots are detached and all I/O is replaced by recording callbacks.
public sealed class OwnerConsoleFieldProducerTests
{
    [Fact]
    public void PrefixOnlyActivityAndGateDoNotInventCanonicalIds()
    {
        var activity = new OwnerConsoleActivityItem(DateTimeOffset.UnixEpoch, "event", "tag", "12345678", "detail");
        Assert.Null(activity.Fields().Fields.Single(field => field.Key == "goal").Value);
        var gate = OwnerConsoleGateProgress.MemberFields(new("12345678", "Title"));
        Assert.Null(gate.Fields.Single(field => field.Key == "goal").Value);
        Assert.Equal("12345678", gate.Fields.Single(field => field.Key == "prefix").Value);
        var writes = 0;
        var result = new OwnerConsoleClipboard(_ => { writes++; return true; }).Copy(gate.Fields[0]);
        Assert.False(result.Copied); Assert.Equal(0, writes);
    }

    [Fact]
    public void ResolutionValuesRemainSeparateAndComplete()
    {
        var resolution = new OwnerQuestionResolution("question-id", OwnerConsoleFieldCopyScene.FirstId, "Full title", "Tester",
            "  Question 日本語\nnext  ", "Answered", "owner", DateTimeOffset.UnixEpoch, "", ["C:\\full path\\evidence"], "Developer");
        var snapshot = OwnerQuestionResolutionText.Fields(resolution);
        Assert.Equal(resolution.Question, snapshot.Fields.Single(field => field.Key == "question").Value);
        Assert.Equal("", snapshot.Fields.Single(field => field.Key == "answer").Value);
        Assert.Equal(resolution.ResolvedAt.ToString("O"), snapshot.Fields.Single(field => field.Key == "at").Value);
        Assert.Equal(snapshot.Fields.Length, snapshot.Fields.Select(field => field.Key).Distinct().Count());
    }

    [Fact]
    public void FleetProjectionKeepsOriginalValuesAlongsideNarrowDiagram()
    {
        var executor = new OwnerFleetExecutorNode("executor-with-a-very-long-full-identity", OwnerFleetReachability.Reachable,
            [" Unicode λ\nmultiline "], DateTimeOffset.UnixEpoch, 2, 1, "passed", DateTimeOffset.UnixEpoch, 0.123456789, []);
        var hold = new OwnerFleetProviderHoldNode("binding/full", "provider", "11111111/22222222", DateTimeOffset.UnixEpoch,
            null, null, null, null) { SourceGoalId = OwnerConsoleFieldCopyScene.FirstId, SourceTaskId = OwnerConsoleFieldCopyScene.SecondId };
        var rows = new Dictionary<int, OwnerConsoleFieldSnapshot>();
        var diagram = OwnerFleetDiagram.Render(new(new(true, 1, 0, 0), [executor], [hold]), 40, rows);
        var snapshot = rows.Values.First(row => row.Identity == executor.Id);
        Assert.Equal(executor.Id, snapshot.Fields.Single(field => field.Key == "executor").Value);
        Assert.Equal(executor.ProbeReasons[0], snapshot.Fields.Single(field => field.Key == "reasons").Value);
        Assert.DoesNotContain(executor.Id, diagram);
        Assert.Equal(hold.SourceGoalId, rows.Values.First(row => row.Identity == hold.BindingKey).Fields.Single(field => field.Key == "source-goal").Value);
    }

    [Fact]
    public void GoalProducerPreservesTaskIdsAndUnderlyingValues()
    {
        var harness = new OwnerConsoleHarness();
        var goal = harness.AddGoal(OwnerConsoleFieldCopyScene.FirstId, "Title\nObjective 日本語", AgentRole.Developer);
        var snapshot = new OwnerGoalDetailSnapshot(goal, null, []).Fields();
        Assert.Equal(goal.Objective, snapshot.Fields.Single(field => field.Key == "objective").Value);
        Assert.Contains(snapshot.Fields, field => field.Value == goal.Tasks[0].Id.Value);
        Assert.Equal(snapshot.Fields.Length, snapshot.Fields.Select(field => field.Key).Distinct().Count());
    }

    [Fact]
    public void StructuredCommandWriterAttachesTypedPanelValuesBeforeDisplayNormalization()
    {
        const string rawJudge = "judge | 日本語";
        using var writer = new OwnerConsoleStructuredWriter();
        CliOwnerDigestJudgePanel.WriteText(writer, new([
            new("case-full", OwnerConsoleFieldCopyScene.FirstId, "trigger", DateTimeOffset.UnixEpoch, "terminal",
                [new(rawJudge, true, "valid", "repair", "owner/full")], new(PanelResolutionKind.OperatorRetry, "retry"))], 0));
        var metadata = writer.Build(null)!;
        var row = metadata.Rows!.First(pair => pair.Value.Identity == "case-full");
        Assert.Equal(OwnerConsoleFieldCopyScene.FirstId, row.Value.Fields.Single(field => field.Key == "goal").Value);
        Assert.Contains(row.Value.Fields, field => field.Value == rawJudge);
        var writes = new List<string>();
        using var text = new OwnerConsoleTextDialog("Command", writer.ToString()) { Structured = metadata };
        text.Page.Select(text.Page.SourceLines.ToList().FindIndex(source => source == row.Key));
        OwnerConsoleFieldSnapshot? opened = null;
        text.FieldsRequested += fields => opened = fields;
        Assert.True(text.HandleKey(OwnerConsoleBindings.OpenFields));
        Assert.NotNull(opened);
        using var modal = new OwnerConsoleFieldsDialog(opened, new(value => { writes.Add(value); return true; }), rawJudge + "/name");
        modal.Copy(); Assert.Equal(rawJudge, Assert.Single(writes));
    }

    [Fact]
    public void EmptyLandingsStillExposeStructuredUndatedCount()
    {
        var page = OwnerLandingsPage.From(new(false, "today", [], 4));
        using var dialog = new OwnerConsoleTextDialog("Landings", page.Text) { Structured = page.Fields };
        OwnerConsoleFieldSnapshot? opened = null; dialog.FieldsRequested += fields => opened = fields;
        Assert.True(dialog.HandleKey(OwnerConsoleBindings.OpenFields));
        Assert.NotNull(opened);
        Assert.Equal("4", opened.Fields.Single(field => field.Key == "undated").Value);
    }
}
