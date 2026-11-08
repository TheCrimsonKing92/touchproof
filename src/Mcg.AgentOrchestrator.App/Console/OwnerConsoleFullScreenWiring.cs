using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleFullScreenWiring(
    Label previewBody, Label notice, Label status, ListView decisions, OwnerConsoleBoardPane board,
    ListView activity, TextField command, OwnerConsoleActivityPane activityPane,
    OwnerConsoleScreenController controller, OwnerConsoleInputDiagnostics? diagnostics,
    Func<OwnerConsoleTraceState> traceState,
    OwnerConsoleRenderState state, Action<bool> setCopySelectionValid,
    Func<string?> completionHint, Action<string?> setCompletionHint,
    Action renderPreview, Action renderNotices, Action renderStatus, Action renderHints,
    Action renderOverflow, Action renderDecisions, Action<int> selectBoard,
    Action<View, OwnerConsolePane> paneFocusChanged, Action<int, int> fit, Func<int?> activityRow)
{
    internal void Attach()
    {
        previewBody.ViewportChanged += (_, _) => renderPreview();
        renderPreview();
        notice.ViewportChanged += (_, _) => renderNotices();
        status.ViewportChanged += (_, _) => renderStatus();
        AttachSelection();
        AttachLayout();
    }

    private void AttachSelection()
    {
        decisions.ValueChanged += (_, _) =>
        {
            if (!state.Rendering && decisions.SelectedItem is { } index) controller.SelectIndex(index);
            if (!state.Rendering) renderHints();
            diagnostics?.Mark("decision-selection", traceState);
        };
        board.ValueChanged += (_, args) =>
        {
            if (!state.Rendering && args.NewValue?.SelectedCell is { } cell)
            {
                setCopySelectionValid(true);
                state.SelectedFieldKey = OwnerConsoleBoardRenderer.FieldKeys.ElementAtOrDefault(cell.X);
                selectBoard(cell.Y);
            }
            diagnostics?.Mark("board-selection", traceState);
        };
        board.MouseEvent += (_, mouse) =>
        {
            // Re-selecting the same numerical cell after its old row disappeared may
            // not raise ValueChanged. A click is still an explicit choice of the new item.
            if (state.Rendering || !mouse.Flags.HasFlag(MouseFlags.LeftButtonClicked) || mouse.Position is not { } position ||
                board.ScreenToCell(position.X, position.Y) is not { } cell) return;
            state.SelectedFieldKey = OwnerConsoleBoardRenderer.FieldKeys.ElementAtOrDefault(cell.X);
            setCopySelectionValid(true);
            selectBoard(cell.Y);
        };
        activity.ValueChanged += (_, _) =>
        {
            if (!state.Rendering && activityRow() is { } row) { activityPane.Inspect(row); renderHints(); renderOverflow(); }
            diagnostics?.Mark("activity-selection", traceState);
        };
        activity.ViewportChanged += (_, args) =>
        {
            var viewport = activity.Viewport; // NewViewport is empty before Terminal.Gui initialization.
            if (!state.Rendering && args.OldViewport.Size == viewport.Size && args.OldViewport.X == viewport.X && args.OldViewport.Y != viewport.Y && activityRow() is { } row)
            { activityPane.Scrolled(row); renderHints(); renderOverflow(); }
        };
        decisions.HasFocusChanged += (_, _) => paneFocusChanged(decisions, OwnerConsolePane.Decisions);
        board.HasFocusChanged += (_, _) => paneFocusChanged(board, OwnerConsolePane.Board);
        activity.HasFocusChanged += (_, _) => paneFocusChanged(activity, OwnerConsolePane.Activity);
        command.HasFocusChanged += (_, _) => renderHints();
        command.TextChanged += (_, _) =>
        {
            if (completionHint() is null) return;
            setCompletionHint(null);
            renderHints();
        };
    }

    private void AttachLayout()
    {
        activity.ViewportChanged += (_, _) =>
        {
            using var trace = diagnostics?.Begin("activity-viewport", traceState);
            fit(activity.Viewport.Width, state.BoardTitleWidth);
            renderOverflow();
        };
        board.ViewportChanged += (_, _) =>
        {
            using var trace = diagnostics?.Begin("board-viewport", traceState);
            fit(state.ActivityWidth, 0);
            renderOverflow();
        };
        decisions.ViewportChanged += (_, _) =>
        {
            using var trace = diagnostics?.Begin("decisions-viewport", traceState);
            if (state.Rendering) return;
            state.Rendering = true;
            try { renderDecisions(); }
            finally { state.Rendering = false; }
            renderOverflow();
        };
    }
}
