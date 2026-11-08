using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.Views;

// Parallel-safe: instance-owned frames, fixed clock and synchronous preview inputs; no GUI globals or I/O.
public sealed class OwnerConsolePreviewPaneTests
{
    private const string Id = "11111111-selected";

    [Fact]
    public void Render_NoSelectedGoal_ShowsExactPromptAndTitle()
    {
        using var scene = new Scene();

        scene.Pane.Render();

        Assert.Equal("SELECTED", scene.Frame.Title);
        Assert.Equal("Select a goal", scene.Pane.Body.Text);
    }

    [Fact]
    public void Render_ForeignRow_ShowsExactRefusalAndGoalTitle()
    {
        using var scene = new Scene
        {
            Selected = new("22222222", "", "Other goal", "Active", "Developer", null, "22222222-foreign")
            { Project = "other", IsForeign = true }
        };

        scene.Pane.Render();

        Assert.Equal("SELECTED 22222222", scene.Frame.Title);
        Assert.Equal("22222222 belongs to other. Detail and actions for other projects arrive in a later slice; open it with --project=other console.",
            scene.Pane.Body.Text);
    }

    [Fact]
    public void Render_DiagnosticRow_ShowsExactDiagnosticAndPlainTitle()
    {
        using var scene = new Scene
        {
            Selected = new("", "", "Project read failed: offline", "", "", null, "diagnostic-row")
            { Project = "other", IsDiagnostic = true }
        };

        scene.Pane.Render();

        Assert.Equal("SELECTED", scene.Frame.Title);
        Assert.Equal("Project read failed: offline", scene.Pane.Body.Text);
    }

    [Fact]
    public void Apply_RefreshFailure_RetainsExactLastGoodBodyAndMarksTitle()
    {
        using var scene = new Scene
        {
            Selected = new("11111111", "", "Board title", "Active", "Developer", null, Id)
            { Project = "home" }
        };
        var section = new OwnerGoalSummarySection("none", [], "test");
        var summary = new OwnerGoalChangeSummary(Id, 1, "criteria", null,
            section, section, section, section, "pending")
        {
            Preview = new("Summary title", new(OwnerGoalPreviewChangeKind.None, [], 0), null)
        };
        scene.Pane.Apply(new(Id, summary));
        Assert.Equal("SELECTED 11111111", scene.Frame.Title);
        Assert.Equal("Summary title\nNo committed changes yet\nDeveloper", scene.Pane.Body.Text);
        scene.Selected = scene.Selected! with { Stage = "Reviewer" };

        scene.Pane.Apply(new(Id, null, "offline"));

        Assert.Equal("SELECTED 11111111 (refresh failed: offline)", scene.Frame.Title);
        Assert.Equal("Summary title\nNo committed changes yet\nDeveloper", scene.Pane.Body.Text);
    }

    private sealed class Scene : IDisposable
    {
        internal FrameView Frame { get; } = new() { Width = 80, Height = 5 };
        internal OwnerConsolePreviewPane Pane { get; }
        internal OwnerConsoleBoardRow? Selected { get; set; }

        internal Scene()
        {
            Pane = new(Frame, new OwnerConsoleTestClock(),
                (_, _) => throw new InvalidOperationException("Direct rendering must not load a summary."),
                action => action(), () => Selected, id => Selected?.GoalId == id ? Selected : null,
                TestContext.Current.CancellationToken);
            Frame.Layout(new Size(80, 5));
            Assert.True(Pane.Body.Viewport.Width > 0, "Preview label must have a laid-out width.");
        }

        public void Dispose()
        {
            Pane.Dispose();
            Frame.Dispose();
        }
    }
}
