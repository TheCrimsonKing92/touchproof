using System.Data;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsolePaneRenderer
{
    private readonly OwnerConsoleRenderState _state;
    private readonly OwnerConsoleScreenController _controller;
    private readonly OwnerConsoleInputDiagnostics? _diagnostics;
    private readonly OwnerConsoleViewTrace _trace;
    private readonly OwnerConsoleBoardCollapse _collapse;
    private readonly TimeProvider _clock;
    private readonly OwnerConsoleRenderCoordinator _renderCoordinator;
    private readonly OwnerConsoleBoardSelection _selection;
    private readonly OwnerConsoleFullScreenLayout _layout;
    private readonly OwnerConsoleBoardPane _board;
    private readonly OwnerConsoleDecisionsPane _decisionsPane;
    private readonly ListView _decisions;
    private readonly OwnerConsoleActivityPane _activityPane;
    private readonly OwnerConsolePreviewPane _previewPane;
    private readonly Action _renderHints;
    private OwnerConsoleGateDialog GateView { get; }
    private Window Window { get; }
    private DataTable BoardTable { get; }
    private OwnerConsoleNoticeStrip NoticeStrip { get; }
    private string? SelectedGoalId => _selection.GoalId;
    private string? SelectedProject => _selection.Project;

    internal OwnerConsolePaneRenderer(OwnerConsoleRenderState state, OwnerConsoleScreenController controller,
        OwnerConsoleInputDiagnostics? diagnostics, OwnerConsoleViewTrace trace, OwnerConsoleBoardCollapse collapse,
        TimeProvider clock, OwnerConsoleRenderCoordinator renderCoordinator, OwnerConsoleBoardSelection selection,
        OwnerConsoleFullScreenLayout layout, OwnerConsoleBoardPane board, OwnerConsoleDecisionsPane decisionsPane,
        OwnerConsoleActivityPane activityPane, OwnerConsolePreviewPane previewPane, Action renderHints,
        OwnerConsoleGateDialog gateView, Window window, DataTable boardTable, OwnerConsoleNoticeStrip noticeStrip)
    {
        _state = state;
        _controller = controller;
        _diagnostics = diagnostics;
        _trace = trace;
        _collapse = collapse;
        _clock = clock;
        _renderCoordinator = renderCoordinator;
        _selection = selection;
        _layout = layout;
        _board = board;
        _decisionsPane = decisionsPane;
        _decisions = decisionsPane.List;
        _activityPane = activityPane;
        _previewPane = previewPane;
        _renderHints = renderHints;
        GateView = gateView;
        Window = window;
        BoardTable = boardTable;
        NoticeStrip = noticeStrip;
    }

    internal void Render(OwnerConsoleViewModel model, bool refreshPreview = true)
    {
        GateView.Render(model.Gate);
        using var trace = _diagnostics?.Begin("render", _trace.State);
        _state.Rendering = true;
        try
        {
            _state.ArrangedBoard = OwnerConsoleBoardSection.Arrange(model);
            model = _collapse.Apply(_state.ArrangedBoard);
            _controller.Apply(model);
            if (model.BoardScope is { } scope) Window.Title = $"Owner console - {OwnerConsoleProjectLabel.ForScope(scope)}";
            _state.RefreshedAt = _clock.GetUtcNow();
            RenderStatus();
            RenderDecisions();
            RenderBoard();
            var selectedHeader = OwnerConsoleBoardCollapse.Enabled(model) && SelectedGoalId is null && SelectedProject is not null;
            var index = Array.FindIndex(model.Board.ToArray(), row =>
                (selectedHeader ? row.IsDiagnostic : row.GoalId == SelectedGoalId) &&
                string.Equals(row.Project, SelectedProject, StringComparison.OrdinalIgnoreCase));
            if (SelectedGoalId is not null && index < 0) _selection.CopyValid = false;
            SelectBoard(index < 0 ? _selection.Index : index, refresh: true,
                allowHeader: selectedHeader && index >= 0, refreshPreview: refreshPreview);
            RenderActivity();
            RenderHints();
            NoticeStrip.RefreshSucceeded();
            RenderNotices();
            RenderOverflow();
        }
        finally { _state.Rendering = false; }
    }

    internal void Fit(int activityWidth, int boardTitleWidth)
    {
        using var trace = _diagnostics?.Begin("fit", _trace.State);
        if (_state.Rendering) { if (trace is not null) trace.Outcome = "skipped-rendering"; return; }
        _state.ActivityWidth = activityWidth;
        _state.BoardTitleWidth = boardTitleWidth;
        _state.Rendering = true;
        try { RenderActivity(); RenderBoard(); }
        finally { _state.Rendering = false; }
        RenderOverflow();
    }

    public void RenderBoard()
    {
        using var trace = _diagnostics?.Begin("board-source-replacement", _trace.State);
        if (_controller.Model is not { } model) return;
        _layout.BoardFooterSegments = OwnerConsoleBoardSection.Footer(model);
        var footerVisible = _layout.BoardFooterSegments.Count > 0;
        if (_layout.BoardFooter.Visible != footerVisible)
        {
            _layout.BoardFooter.Visible = footerVisible;
            _board.Height = Dim.Fill(footerVisible ? 1 : 0);
        }
        _layout.BoardFooter.SetNeedsDraw();
        OwnerConsoleBoardRenderer.Render(BoardTable, _board, model.Board, _board.Viewport.Width, _state.BoardTitleWidth,
            model.BoardScope is { Sections: not null });
        var fieldColumn = OwnerConsoleBoardRenderer.FieldKeys.ToList().IndexOf(_state.SelectedFieldKey ?? "");
        if (fieldColumn < 0 || !_board.Style.GetOrCreateColumnStyle(fieldColumn).Visible) _state.SelectedFieldKey = null;
        if (fieldColumn >= 0 && _selection.Index >= 0 && _selection.Index < BoardTable.Rows.Count)
            _board.SetSelection(fieldColumn, _selection.Index, false);
        RenderPreview();
    }

    internal void RenderDecisions()
    {
        using var trace = _diagnostics?.Begin("decisions-source-replacement", _trace.State);
        if (_controller.Model is not { } model) return;
        _decisionsPane.Render(model, _controller.SelectedIndex, _decisions.Viewport.Width);
        RenderOverflow();
    }

    private void RenderActivity()
    {
        using var trace = _diagnostics?.Begin("activity-source-replacement", _trace.State);
        _activityPane.Render(_controller.Model, _state.ActivityWidth);
    }

    private void RenderStatus() => _renderCoordinator.RenderStatus();
    private void RenderNotices() => _renderCoordinator.RenderNotices();
    private void RenderOverflow() => _renderCoordinator.RenderOverflow();
    private void RenderPreview() => _previewPane.Render();
    private void RenderHints() => _renderHints();
    private void SelectBoard(int index, bool refresh = false, int direction = 0, bool allowHeader = false,
        bool refreshPreview = true) =>
        _selection.Select(index, refresh, direction, allowHeader, refreshPreview);
}
