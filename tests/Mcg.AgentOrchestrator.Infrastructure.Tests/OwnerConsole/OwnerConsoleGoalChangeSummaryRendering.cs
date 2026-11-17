using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using TestGoal = OwnerConsoleGoalChangeSummaryTestGoal;
using Queries = OwnerConsoleGoalChangeSummaryQueries;
using EmptyTail = OwnerConsoleGoalChangeSummaryEmptyTail;
using Dialogs = OwnerConsoleGoalChangeSummaryDialogs;
using static OwnerConsoleGoalChangeSummaryFixtures;

internal static class OwnerConsoleGoalChangeSummaryRendering
{
    internal static async Task<(string Preview, string Detail)> RenderSelectedGoalAsync(
        TestGoal goal, OwnerGoalChangeSummaryQuery summary)
    {
        var layoutComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var queries = new Queries(goal, layoutComplete.Task);
        var detailQuery = new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: summary);
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs, detailQuery);
        using IApplication app = Terminal.Gui.App.Application.Create();
        Assert.False(app.Initialized);
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(Model(goal));
        var previewLoad = view.PreviewLoad;
        try
        {
            // Without a UI dispatcher, publication runs inline on the loader thread.
            // Hold the query until the initial layout has finished rendering the label.
            Assert.False(previewLoad.IsCompleted);
            view.Window.Layout(new Size(100, 40));
            Assert.Equal(string.Join('\n', Model(goal).Board[0].Title, Model(goal).Board[0].Stage), view.PreviewText);
            Assert.Same(previewLoad, view.PreviewLoad);
            Assert.False(previewLoad.IsCompleted);
        }
        finally { layoutComplete.TrySetResult(); }
        await previewLoad.WaitAsync(Token);
        var preview = view.PreviewText;
        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);
        Assert.Equal(new[] { goal.Id.Value, goal.Id.Value }, queries.ReadIds);
        Assert.Equal(0, queries.MetadataReads);
        Assert.Empty(harness.Answers.Calls); Assert.Empty(harness.Conductor.Calls);
        return (preview, Assert.Single(dialogs.Texts));
    }
}
