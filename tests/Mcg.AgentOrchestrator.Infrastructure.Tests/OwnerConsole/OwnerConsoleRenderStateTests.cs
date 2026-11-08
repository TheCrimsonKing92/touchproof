using System.Data;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;

// Parallel-safe: each test owns an uninitialized app and views; no global driver or terminal I/O.
public sealed class OwnerConsoleRenderStateTests
{
    [Fact]
    public void Fit_WhileRendering_PreservesWidthsUntilRenderingEnds()
    {
        var harness = Harness();
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, Controller(harness), () => Task.CompletedTask);
        var surface = (IOwnerConsoleBindingSurface)view;
        view.Fit(50, 0);
        ((IOwnerConsoleKeySurface)view).Rendering = true;

        view.Fit(7, 3);

        Assert.Equal(50, surface.ActivityWidth);
        ((IOwnerConsoleKeySurface)view).Rendering = false;
        view.Fit(7, 3);
        Assert.Equal(7, surface.ActivityWidth);
    }

    [Fact]
    public async Task Render_ActivityPaneThrows_AllowsNextFit()
    {
        var harness = Harness();
        var controller = Controller(harness);
        var model = await Model(harness);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Fit(50, 0);

        // Apply succeeds with this model; enumeration of default Activity fails inside the pane.
        controller.Apply(model with { Activity = default });
        var error = Assert.ThrowsAny<Exception>(() => view.Render(model with { Activity = default }));
        Assert.Contains("OwnerConsoleActivityPane.Render", error.StackTrace);

        controller.Apply(model);
        view.Fit(33, 0);
        Assert.Equal(33, ((IOwnerConsoleBindingSurface)view).ActivityWidth);
    }

    [Fact]
    public async Task Fit_ActivityPaneThrows_AllowsNextFit()
    {
        var harness = Harness();
        var controller = Controller(harness);
        var model = await Model(harness);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(model);
        controller.Apply(model with { Activity = default });

        var error = Assert.ThrowsAny<Exception>(() => view.Fit(21, 0));
        Assert.Contains("OwnerConsoleActivityPane.Render", error.StackTrace);

        controller.Apply(model);
        view.Fit(34, 0);
        Assert.Equal(34, ((IOwnerConsoleBindingSurface)view).ActivityWidth);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Render_FullScreenFixture_MatchesPaneOutput(bool populated)
    {
        var harness = Harness();
        if (populated)
        {
            harness.AddGoal("11111111", "Ship owner console", AgentRole.Developer);
            harness.AddGoal("22222222", "Review owner console", AgentRole.Reviewer);
        }
        var model = await Model(harness);
        if (populated)
            model = model with { Activity = [
                new(harness.Clock.GetUtcNow(), "task", "working", "11111111", "Rendering owner console"),
                new(harness.Clock.GetUtcNow().AddMinutes(-1), "task", "done", "22222222", "Review complete")] };
        var controller = Controller(harness);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Fit(50, 12);

        view.Render(model);

        var expectedModel = new OwnerConsoleBoardCollapse().Apply(OwnerConsoleBoardSection.Arrange(model));
        var decisions = new OwnerConsoleDecisionsPane();
        using var decisionsList = decisions.List;
        using var decisionsEmpty = decisions.EmptyLabel;
        decisions.Render(expectedModel, controller.SelectedIndex, view.DecisionsPane.Viewport.Width);
        Assert.Equal(2, decisions.Lines.Count);
        Assert.Equal(decisions.Lines, view.DecisionLines);

        using var table = new DataTable();
        foreach (var name in new[] { "GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "AGE" }) table.Columns.Add(name);
        using var board = new OwnerConsoleBoardPane();
        OwnerConsoleBoardRenderer.Render(table, board, expectedModel.Board, view.BoardPane.Viewport.Width, 12,
            expectedModel.BoardScope is { Sections: not null });
        Assert.Equal(populated ? 2 : 0, table.Rows.Count);
        Assert.Equal(table.Rows.Count, view.BoardTable.Rows.Count);
        for (var index = 0; index < table.Rows.Count; index++)
            Assert.Equal(table.Rows[index].ItemArray, view.BoardTable.Rows[index].ItemArray);

        var activity = new OwnerConsoleActivityPane();
        using var activityList = activity.List;
        activity.Render(expectedModel, ((IOwnerConsoleBindingSurface)view).ActivityWidth);
        // Both fixtures have activity: owner questions produce lines even without board goals.
        Assert.NotEmpty(activity.Lines);
        Assert.Equal(activity.Lines, view.ActivityLines);
        view.Fit(37, 0);
        Assert.Equal(37, ((IOwnerConsoleBindingSurface)view).ActivityWidth);
    }

    // Same q1/q2 inputs and builder as OwnerConsoleFullScreenTests' private fixture helpers.
    private static OwnerConsoleHarness Harness()
    {
        var harness = new OwnerConsoleHarness();
        harness.Questions.Items.Add(new("q1", "11111111", OwnerQuestionKind.HumanInput, "Ship?\nFull context", "repo", "high", "ship"));
        harness.Questions.Items.Add(new("q2", "11111111", OwnerQuestionKind.Clarification, "Which branch?"));
        return harness;
    }

    private static OwnerConsoleScreenController Controller(OwnerConsoleHarness harness) =>
        OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, new Dialogs(), harness.State,
            harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);

    private static Task<OwnerConsoleViewModel> Model(OwnerConsoleHarness harness) =>
        new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness, new Epics(), harness.Clock)
            .BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0));

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
    }
}
