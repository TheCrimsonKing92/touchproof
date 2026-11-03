using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;

// Parallel-safe: in-memory models and event-gated modal completion, no real clipboard.
public sealed class OwnerConsoleFieldCopyLifecycleTests
{
    [Fact]
    public async Task OpenSnapshotRetainsIdentitySchemaAndValuesAcrossRefreshAndRemoval()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(Key.CursorRight);
        await scene.View.HandleKeyAsync(Key.CursorRight);
        scene.Dialog.Hold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var open = scene.View.HandleKeyAsync(OwnerConsoleBindings.OpenFields);
        var modal = Assert.IsType<OwnerConsoleFieldsDialog>(scene.Dialog.Fields);
        var captured = modal.Snapshot;
        Assert.Equal("title", modal.SelectedField?.Key);
        scene.View.Render(scene.Model with { Board = [scene.Model.Board[1], scene.Model.Board[0] with { Title = "changed", Priority = "Normal" }] });
        Assert.Equal(OwnerConsoleFieldCopyScene.FirstId, scene.View.SelectedGoalId);
        Assert.Equal(1, scene.View.BoardPane.Value?.SelectedCell.Y);
        scene.View.Render(scene.Model with { Board = [scene.Model.Board[1]] });
        modal.HandleKey(OwnerConsoleBindings.CopyField);
        Assert.Equal(scene.Model.Board[0].Title, Assert.Single(scene.Copies));
        Assert.Equal(captured, modal.Snapshot);
        Assert.Equal(OwnerConsoleFieldCopyScene.FirstId, captured.Fields.Single(field => field.Key == "goal").Value);
        modal.HandleKey(Key.Esc);
        await open.WaitAsync(TimeSpan.FromSeconds(30)); // Hang-only: Fields close signal did not complete.
        Assert.True(scene.View.BoardPane.HasFocus);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.CopyField);
        Assert.Single(scene.Copies); // A removed row cannot silently retarget the clipboard.
        Assert.Contains("No selected field", scene.View.NoticeText);
        await scene.View.HandleKeyAsync(Key.Home);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.CopyField);
        Assert.Equal("Second title", scene.Copies[^1]);
        Assert.Empty(scene.Harness.Answers.Calls);
        Assert.Equal(0, scene.Dialog.TextOpens);
    }

    [Fact]
    public async Task ColumnIdentitySurvivesRefreshButHiddenFieldNeedsExplicitSelection()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        await scene.View.HandleKeyAsync(Key.Tab);
        scene.View.BoardPane.SetSelection(3, 0, false);
        scene.View.Render(scene.Model with { Board = [scene.Model.Board[1], scene.Model.Board[0]] });
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.CopyField);
        Assert.Equal("Critical", Assert.Single(scene.Copies));
        scene.View.BoardPane.Frame = new(0, 0, 40, 8);
        scene.View.Fit(40, 10);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.CopyField);
        Assert.Single(scene.Copies);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.OpenFields);
        Assert.Contains(scene.Dialog.Fields!.Snapshot.Fields, field => field.Key == "priority" && field.Value == "Critical");
    }

    [Fact]
    public async Task CommandEditorRetainsShortcutsAndNoCopyFallsThrough()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(new Key(':'));
        Assert.True(scene.View.CommandLine.HasFocus);
        scene.View.CommandLine.Text = "some selected text";
        var copy = Key.C.WithCtrl;
        await scene.View.HandleKeyAsync(copy);
        var fields = Key.F2;
        await scene.View.HandleKeyAsync(fields);
        Assert.False(copy.Handled);
        Assert.False(fields.Handled);
        Assert.Empty(scene.Copies);
        Assert.Equal(0, scene.Dialog.FieldOpens);
        Assert.Equal("some selected text", scene.View.CommandLine.Text);
        Assert.Empty(scene.Harness.Answers.Calls);
        Assert.Equal(0, scene.Dialog.TextOpens);
    }

    [Fact]
    public void TextDialogDisposalStopsFieldAndRowActions()
    {
        var fields = 0; var actions = 0;
        var dialog = new OwnerConsoleTextDialog("Rows", "first\nsecond", [0, 1])
        { Structured = new(OwnerConsoleFieldSnapshot.Create("first", new OwnerConsoleField("id", "ID", "first"))) };
        dialog.FieldsRequested += _ => fields++;
        dialog.ChoiceAccepted += _ => actions++;
        dialog.HandleKey(OwnerConsoleBindings.OpenFields);
        Assert.Equal(1, fields); Assert.Equal(0, actions);
        dialog.Dispose();
        Assert.False(dialog.HandleKey(OwnerConsoleBindings.OpenFields));
        Assert.False(dialog.HandleKey(Key.Enter));
        Assert.Equal(1, fields); Assert.Equal(0, actions);
    }

    [Fact]
    public void AnswerEditorFocusDoesNotForwardCopyOrF2ToStructuredBody()
    {
        using var dialog = new OwnerConsoleTextDialog("Answer", "question")
        { Structured = new(OwnerConsoleFieldSnapshot.Create("question", new OwnerConsoleField("id", "ID", "question"))) };
        using var editor = new Terminal.Gui.Views.TextField { Text = "selected answer" };
        dialog.Add(editor); editor.SetFocus();
        var fields = 0; dialog.FieldsRequested += _ => fields++;
        Assert.True(editor.HasFocus); Assert.False(dialog.Body.HasFocus);
        var copy = Key.C.WithCtrl; var f2 = Key.F2;
        Assert.False(dialog.HandleFocusedKey(copy)); Assert.False(dialog.HandleFocusedKey(f2));
        Assert.False(copy.Handled); Assert.False(f2.Handled); Assert.Equal(0, fields);
        Assert.Equal("selected answer", editor.Text);
        dialog.Body.SetFocus(); Assert.True(dialog.HandleFocusedKey(Key.F2)); Assert.Equal(1, fields);
        dialog.Remove(editor);
    }

    [Fact]
    public async Task UnavailableEpicFieldsExplainAbsenceAndDoNotTargetBackgroundBoard()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        await scene.View.HandleKeyAsync(new Key('e'));
        Assert.True(scene.View.EpicView.IsOpen);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.OpenFields);
        Assert.Contains("No selected structured item", scene.View.EpicView.FieldsHintText);
        Assert.Equal(0, scene.Dialog.FieldOpens); Assert.Empty(scene.Copies);
    }
}
