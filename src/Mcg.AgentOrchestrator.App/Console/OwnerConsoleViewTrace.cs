using Terminal.Gui.App;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleViewTrace(
    IOwnerConsoleKeySurface surface, IApplication app, Window window,
    OwnerConsoleScreenController controller, OwnerConsoleInputDiagnostics? diagnostics)
{
    internal IDisposable? BeginActivitySourceReplacement() => diagnostics?.Begin("activity-source-replacement", State);

    internal void OnLayoutAndDrawComplete(object? sender, EventArgs args) =>
        diagnostics?.Mark("layout-draw-complete", State);

    internal OwnerConsoleTraceState State()
    {
        static OwnerConsoleTracePane Pane(View view, int index, string? identity, int count) =>
            new(index, identity, count, view.Viewport.X, view.Viewport.Y, view.Viewport.Width, view.Viewport.Height);
        return new(surface.EditingCommand || surface.CommandLine.HasFocus ? "Command" : surface.FocusedPane.ToString(),
            surface.EditingCommand, surface.EpicView.IsOpen || surface.GateView.IsOpen || (app.Initialized && app.TopRunnableView != window),
            Pane(surface.DecisionsPane, surface.DecisionsPane.SelectedItem ?? -1, controller.SelectedDecisionId, controller.Model?.Decisions.Length ?? 0),
            Pane(surface.BoardPane, surface.SelectedBoardIndex, surface.SelectedGoalId, controller.Model?.Board.Length ?? 0) with
                { RowOffset = surface.BoardPane.RowOffset, ColumnOffset = surface.BoardPane.ColumnOffset },
            Pane(surface.ActivityPane, surface.ActivityPane.SelectedItem ?? -1, null, surface.ActivityLines.Count));
    }
}
