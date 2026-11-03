using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Input;

// Parallel-safe: instance applications, in-memory rows and recording clipboard sinks.
public sealed class OwnerConsoleBoardFieldCopyTests
{
    [Fact]
    public async Task KeyboardCopiesFullCellsAndPreservesRowNavigationAndEnter()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        await scene.View.HandleKeyAsync(Key.Tab);
        Assert.False(scene.View.BoardPane.FullRowSelect);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.CopyField);
        await scene.View.HandleKeyAsync(Key.CursorRight);
        await scene.View.HandleKeyAsync(Key.CursorRight);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.CopyField);
        Assert.Equal([OwnerConsoleFieldCopyScene.FirstId, scene.Model.Board[0].Title], scene.Copies);
        Assert.NotEqual(scene.View.BoardTable.Rows[0][2], scene.Copies[1]);
        await scene.View.HandleKeyAsync(Key.CursorDown);
        Assert.Equal(2, scene.View.BoardPane.Value?.SelectedCell.X);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.CopyField);
        Assert.Equal("Second title", scene.Copies[^1]);
        Assert.Equal(0, scene.Dialog.TextOpens);
        await scene.View.HandleKeyAsync(Key.Enter);
        Assert.Equal(1, scene.Dialog.TextOpens);
        Assert.Empty(scene.Harness.Answers.Calls);
    }

    [Fact]
    public async Task MouseSelectsOneCellWithoutOpeningRow()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        await scene.View.HandleKeyAsync(Key.Tab);
        var table = scene.View.BoardPane;
        var target = new Point(2, 1);
        var points = from y in Enumerable.Range(0, table.Viewport.Height)
                     from x in Enumerable.Range(0, table.Viewport.Width)
                     where table.ScreenToCell(x, y) == target
                     select new Point(x, y);
        var point = points.First(); // Fails if the real table cannot hit-test the target cell.
        // Direct view dispatch expects viewport coordinates; application dispatch normally supplies them.
        var screenPoint = table.ViewportToScreen(point);
        Assert.NotEqual(point, screenPoint);
        table.NewMouseEvent(new Mouse { Position = point, ScreenPosition = screenPoint, View = table, Flags = MouseFlags.LeftButtonClicked });
        Assert.Equal(target, table.Value?.SelectedCell);
        Assert.Equal(OwnerConsoleFieldCopyScene.SecondId, scene.View.SelectedGoalId);
        Assert.Empty(scene.Copies);
        Assert.Equal(0, scene.Dialog.TextOpens);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.CopyField);
        Assert.Equal("Second title", Assert.Single(scene.Copies));
    }

    [Fact]
    public async Task F2IncludesHiddenPriorityAndCopiesOnlyThatValue()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        scene.View.BoardPane.Frame = new Rectangle(0, 0, 45, 8);
        scene.View.Fit(45, 10);
        Assert.False(scene.View.BoardPane.Style.GetOrCreateColumnStyle(3).Visible);
        await scene.View.HandleKeyAsync(Key.Tab);
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.OpenFields);
        var dialog = Assert.IsType<OwnerConsoleFieldsDialog>(scene.Dialog.Fields);
        dialog.Fields.SelectedItem = dialog.Snapshot.Fields.ToList().FindIndex(field => field.Key == "priority");
        dialog.HandleKey(OwnerConsoleBindings.CopyField);
        Assert.Equal("Critical", Assert.Single(scene.Copies));
        Assert.Contains("Copied", dialog.Feedback);
        Assert.Equal(0, scene.Dialog.TextOpens);
    }

    [Fact]
    public async Task ClickingSameCellAfterRemovalExplicitlySelectsReplacement()
    {
        using var scene = new OwnerConsoleFieldCopyScene();
        await scene.View.HandleKeyAsync(Key.Tab);
        var table = scene.View.BoardPane;
        table.SetSelection(2, 0, false);
        scene.View.Render(scene.Model with { Board = [scene.Model.Board[1]] });
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.CopyField);
        Assert.Empty(scene.Copies);
        var point = (from y in Enumerable.Range(0, table.Viewport.Height)
                     from x in Enumerable.Range(0, table.Viewport.Width)
                     where table.ScreenToCell(x, y) == new Point(2, 0)
                     select new Point(x, y)).First();
        var screenPoint = table.ViewportToScreen(point);
        Assert.NotEqual(point, screenPoint);
        table.NewMouseEvent(new Mouse { Position = point, ScreenPosition = screenPoint, View = table, Flags = MouseFlags.LeftButtonClicked });
        await scene.View.HandleKeyAsync(OwnerConsoleBindings.CopyField);
        Assert.Equal("Second title", Assert.Single(scene.Copies));
        Assert.Equal(0, scene.Dialog.TextOpens); Assert.Empty(scene.Harness.Answers.Calls);
    }
}
