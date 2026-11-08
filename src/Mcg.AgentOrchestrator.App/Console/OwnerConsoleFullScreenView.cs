using System.Data;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleFullScreenView : IDisposable, IOwnerConsoleKeySurface, IOwnerConsoleBindingSurface
{
    private readonly OwnerConsoleScreenController _controller;
    private readonly OwnerConsoleKeyRouter _router;
    private readonly OwnerConsoleBindingRunner _bindingRunner;
    private readonly IApplication _app;
    private readonly IKeyboard? _keyboard;
    private readonly OwnerConsoleInputDiagnostics? _diagnostics;
    private readonly Func<Task> _refresh;
    private readonly OwnerConsoleBoardSource? _boardSource;
    private readonly CancellationToken _token;
    private readonly CancellationTokenSource _lifetime;
    private readonly OwnerConsoleNoticeExpiry _noticeExpiry;
    private readonly OwnerStatusPresenter _statusPresenter = new();
    private readonly Label _status = new() { Width = Dim.Fill(), Height = 1 };
    private readonly Label _notice = new() { Y = Pos.AnchorEnd(3), Width = Dim.Fill(), Height = 1 };
    private readonly OwnerConsoleDecisionsPane _decisionsPane = new();
    private readonly OwnerConsoleActivityPane _activityPane = new();
    private readonly ListView _decisions;
    private readonly OwnerConsoleBoardPane _board = new() { Width = Dim.Fill(), Height = Dim.Fill() };
    private readonly OwnerConsoleFullScreenLayout _layout;
    private readonly OwnerConsolePreviewPane _previewPane;
    internal string PreviewText => _previewPane.Body.Text;
    internal Task PreviewLoad => _previewPane.LastLoad;
    private readonly ListView _activity;
    private readonly TextField _command = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private readonly Label _hints = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly Label _epicHint = new() { Y = Pos.AnchorEnd(2), Text = OwnerConsoleEpicFormatter.KeyHint, Height = 1 };
    public OwnerConsoleEpicDialog EpicView { get; }
    public OwnerConsoleGateDialog GateView { get; } = new();
    internal string EpicHintText => _epicHint.Text;
    private readonly Dictionary<string, string> _working = new();
    private readonly OwnerConsoleScreenOperation _operation;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _refreshInterval;
    private DateTimeOffset? _refreshedAt;
    private bool _rendering;
    private bool _editingCommand;
    private string? _completionHint;
    private bool _acting;
    private OwnerConsolePane _focusedPane = OwnerConsolePane.Decisions;
    private int _selectedBoardIndex = -1;
    private readonly OwnerConsoleBoardCollapse _collapse = new();
    private OwnerConsoleViewModel? _arrangedBoard;
    private int _activityWidth;
    private int _boardTitleWidth;
    internal bool ActivityFollowing => _activityPane.Following;
    internal string? ActivityFilter => _activityPane.Filter;
    private View? _commandReturnFocus;
    public bool ActionRunning => _acting || _operation.IsRunning;

    internal Window Window { get; } = new() { Title = "Owner console", Width = Dim.Fill(), Height = Dim.Fill() };
    public DataTable BoardTable { get; } = CreateBoardTable();
    internal string StatusText => _status.Text;
    internal OwnerConsoleNoticeStrip NoticeStrip { get; }
    internal string NoticeText => _notice.Text;
    public TextField CommandLine => _command;
    public TableView BoardPane => _board;
    private readonly OwnerConsoleClipboard _clipboard;
    private string? _selectedFieldKey = "goal";
    private bool _copySelectionValid = true;
    public OwnerConsoleFieldSnapshot? SelectedBoardFields
    {
        get
        {
            if (!_copySelectionValid || _controller.Model is not { } model || _selectedBoardIndex < 0 ||
                _selectedBoardIndex >= model.Board.Length || model.Board[_selectedBoardIndex].IsDiagnostic) return null;
            var row = model.Board[_selectedBoardIndex];
            var snapshot = row.Fields();
            return _previewPane.Row?.Project == row.Project && _previewPane.Preview.GoalId == row.GoalId && _previewPane.Preview.Summary is { } summary
                ? snapshot with { Fields = [.. snapshot.Fields, .. summary.Fields().Select(item => item with { Key = "preview/" + item.Key })] }
                : snapshot;
        }
    }
    public ListView DecisionsPane => _decisions;
    public ListView ActivityPane => _activity;
    internal string HintText => _hints.Text;
    internal IReadOnlyList<string> DecisionLines => _decisionsPane.Lines;
    public IReadOnlyList<string> ActivityLines => _activityPane.Lines;
    public string? SelectedGoalId { get; private set; }
    public string? SelectedProject { get; private set; }
    private OwnerConsoleBoardRow? SelectedBoardRow => _controller.Model is { } model &&
        _selectedBoardIndex >= 0 && _selectedBoardIndex < model.Board.Length ? model.Board[_selectedBoardIndex] : null;
    public OwnerConsolePane FocusedPane => _decisions.HasFocus ? OwnerConsolePane.Decisions :
        _board.HasFocus ? OwnerConsolePane.Board : _activity.HasFocus ? OwnerConsolePane.Activity : _focusedPane;

    public bool EditingCommand { get => _editingCommand; set => _editingCommand = value; }
    string? IOwnerConsoleKeySurface.CompletionHint { set => _completionHint = value; }
    public View? CommandReturnFocus { get => _commandReturnFocus; set => _commandReturnFocus = value; }
    int IOwnerConsoleKeySurface.HintWidth => _hints.Viewport.Width;
    int IOwnerConsoleKeySurface.SelectedBoardIndex => _selectedBoardIndex;
    string? IOwnerConsoleKeySurface.SelectedFieldKey { get => _selectedFieldKey; set => _selectedFieldKey = value; }
    bool IOwnerConsoleKeySurface.CopySelectionValid { set => _copySelectionValid = value; }
    OwnerConsoleViewModel? IOwnerConsoleKeySurface.ArrangedBoard => _arrangedBoard;
    public bool Rendering { set => _rendering = value; }
    public OwnerConsoleActivityPane ActivityModel => _activityPane;
    OwnerConsoleFieldSnapshot? IOwnerConsoleKeySurface.DecisionFields => _decisionsPane.SelectedFields;
    int IOwnerConsoleBindingSurface.ActivityWidth => _activityWidth;
    IDisposable? IOwnerConsoleBindingSurface.BeginActivitySourceReplacement() => _diagnostics?.Begin("activity-source-replacement", TraceState);
    void IOwnerConsoleBindingSurface.RequestStop() => _app.RequestStop(Window);

    internal OwnerConsoleFullScreenView(IApplication app, OwnerConsoleScreenController controller,
        Func<Task> refresh, CancellationToken token = default, TimeProvider? clock = null,
        OwnerConsoleLoopOptions? options = null, OwnerConsoleInputDiagnostics? diagnostics = null, string? projectLabel = null,
        OwnerConsoleBoardSource? boardSource = null, OwnerConsoleClipboard? clipboard = null)
    {
        _decisions = _decisionsPane.List;
        _activity = _activityPane.List;
        if (projectLabel is not null) Window.Title = $"Owner console - {projectLabel}";
        _app = app;
        _clipboard = clipboard ?? OwnerConsoleClipboard.For(app);
        _diagnostics = diagnostics;
        _controller = controller;
        _refresh = refresh;
        _boardSource = boardSource;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _lifetime.Token;
        _clock = clock ?? TimeProvider.System;
        NoticeStrip = new(_clock);
        _refreshInterval = (options ?? OwnerConsoleLoopOptions.Default).RefreshInterval;
        if (_refreshInterval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(options), "Refresh interval must be positive.");
        _noticeExpiry = new(_clock, Invoke, RenderNotices, _token);
        _operation = new(_clock,
            label => Invoke(() => { if (!_token.IsCancellationRequested) SetWorking("command", label); }),
            message => Invoke(() => { if (!_token.IsCancellationRequested) ShowNotice(message, OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action); }), options,
            message => Invoke(() => { if (!_token.IsCancellationRequested) ShowNotice(message, OwnerConsoleNoticeSeverity.Success, OwnerConsoleNoticeSource.Action); }));
        _layout = new(_decisions, _decisionsPane.EmptyLabel, _board, _activity);
        _previewPane = new(_layout.PreviewFrame, _clock, controller.LoadGoalSummaryAsync, Invoke,
            () => SelectedBoardRow, id => _controller.Model?.Board.FirstOrDefault(row =>
                row.Project == SelectedProject && row.GoalId == id), _token);
        _board.Table = new DataTableSource(BoardTable);
        _epicHint.Width = OwnerConsoleEpicFormatter.KeyHint.Length;
        _epicHint.X = Pos.AnchorEnd(OwnerConsoleEpicFormatter.KeyHint.Length);
        _hints.Width = Dim.Fill(OwnerConsoleEpicFormatter.KeyHint.Length + 2);
        EpicView = new(controller, Invoke, token);
        _layout.Compose(Window, _status, _notice, _hints, _epicHint, _command, EpicView, GateView);
        new OwnerConsoleFullScreenWiring(
            _previewPane.Body, _notice, _status, _decisions, _board, _activity, _command, _activityPane,
            _controller, _diagnostics, TraceState,
            () => _rendering, value => _rendering = value, value => _copySelectionValid = value,
            value => _selectedFieldKey = value, () => _completionHint, value => _completionHint = value,
            () => _activityWidth, () => _boardTitleWidth,
            RenderPreview, RenderNotices, RenderStatus, RenderHints, RenderOverflow, RenderDecisions,
            index => SelectBoard(index), PaneFocusChanged, Fit, ActivityRow).Attach();
        RenderHints();
        _router = new(this, _controller, _operation, _clipboard, _collapse, _token);
        _bindingRunner = new(this, _controller, _operation, _boardSource is null ? null : _boardSource.HandleScope, _refresh, _token);
        _keyboard = app.Initialized ? app.Keyboard : null;
        if (_keyboard is not null) _keyboard.KeyDown += OnKeyDown;
        if (_diagnostics is not null) _app.LayoutAndDrawComplete += OnLayoutAndDrawComplete;
    }

    private static DataTable CreateBoardTable()
    {
        var table = new DataTable();
        foreach (var name in new[] { "GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "AGE" }) table.Columns.Add(name);
        return table;
    }

    internal void Render(OwnerConsoleViewModel model)
    {
        GateView.Render(model.Gate);
        using var trace = _diagnostics?.Begin("render", TraceState);
        _rendering = true;
        try
        {
            _arrangedBoard = OwnerConsoleBoardSection.Arrange(model);
            model = _collapse.Apply(_arrangedBoard);
            _controller.Apply(model);
            if (model.BoardScope is { } scope) Window.Title = $"Owner console - {OwnerConsoleProjectLabel.ForScope(scope)}";
            _refreshedAt = _clock.GetUtcNow();
            RenderStatus();
            RenderDecisions();
            RenderBoard();
            var selectedHeader = OwnerConsoleBoardCollapse.Enabled(model) && SelectedGoalId is null && SelectedProject is not null;
            var index = Array.FindIndex(model.Board.ToArray(), row =>
                (selectedHeader ? row.IsDiagnostic : row.GoalId == SelectedGoalId) &&
                string.Equals(row.Project, SelectedProject, StringComparison.OrdinalIgnoreCase));
            if (SelectedGoalId is not null && index < 0) _copySelectionValid = false;
            SelectBoard(index < 0 ? _selectedBoardIndex : index, refresh: true,
                allowHeader: selectedHeader && index >= 0);
            RenderActivity();
            RenderHints();
            NoticeStrip.RefreshSucceeded();
            RenderNotices();
            RenderOverflow();
        }
        finally { _rendering = false; }
    }

    internal void Fit(int activityWidth, int boardTitleWidth)
    {
        using var trace = _diagnostics?.Begin("fit", TraceState);
        if (_rendering) { if (trace is not null) trace.Outcome = "skipped-rendering"; return; }
        _activityWidth = activityWidth;
        _boardTitleWidth = boardTitleWidth;
        _rendering = true;
        try { RenderActivity(); RenderBoard(); }
        finally { _rendering = false; }
        RenderOverflow();
    }

    public void RenderBoard()
    {
        using var trace = _diagnostics?.Begin("board-source-replacement", TraceState);
        if (_controller.Model is not { } model) return;
        _layout.BoardFooterSegments = OwnerConsoleBoardSection.Footer(model);
        var footerVisible = _layout.BoardFooterSegments.Count > 0;
        if (_layout.BoardFooter.Visible != footerVisible)
        {
            _layout.BoardFooter.Visible = footerVisible;
            _board.Height = Dim.Fill(footerVisible ? 1 : 0);
        }
        _layout.BoardFooter.SetNeedsDraw();
        OwnerConsoleBoardRenderer.Render(BoardTable, _board, model.Board, _board.Viewport.Width, _boardTitleWidth,
            model.BoardScope is { Sections: not null });
        var fieldColumn = OwnerConsoleBoardRenderer.FieldKeys.ToList().IndexOf(_selectedFieldKey ?? "");
        if (fieldColumn < 0 || !_board.Style.GetOrCreateColumnStyle(fieldColumn).Visible) _selectedFieldKey = null;
        if (fieldColumn >= 0 && _selectedBoardIndex >= 0 && _selectedBoardIndex < BoardTable.Rows.Count)
            _board.SetSelection(fieldColumn, _selectedBoardIndex, false);
        RenderPreview();
    }

    private void RenderDecisions()
    {
        using var trace = _diagnostics?.Begin("decisions-source-replacement", TraceState);
        if (_controller.Model is not { } model) return;
        _decisionsPane.Render(model, _controller.SelectedIndex, _decisions.Viewport.Width);
        RenderOverflow();
    }

    public int BoardBodyRows() => _board.Viewport.Height - 1 -
        (_board.Style.ShowHorizontalHeaderOverline ? 1 : 0) -
        (_board.Style.ShowHorizontalHeaderUnderline ? 1 : 0);

    public void RenderOverflow()
    {
        var model = _controller.Model;
        SetTitle(_layout.DecisionsFrame, "DECISIONS" + (_decisionsPane.TitleStatus is { } status ? $" ({status})" : ""), OwnerConsoleOverflow.Of(
            _decisions.Source?.Count ?? 0, _decisions.Viewport.Y, _decisions.Viewport.Height));
        SetTitle(_layout.BoardFrame, "BOARD", OwnerConsoleOverflow.Of(
            BoardTable.Rows.Count, _board.RowOffset, BoardBodyRows()));
        var activityTitle = OwnerConsoleActivityTitle.Title(OwnerConsoleOverflow.Of(
            ActivityLines.Count, _activity.Viewport.Y, _activity.Viewport.Height),
            _activityPane.Following, _layout.ActivityFrame.Frame.Width - OwnerConsoleOverflow.TitleChrome,
            _activityPane.Filter, _activityPane.TitleStatus);
        if (_layout.ActivityFrame.Title != activityTitle) _layout.ActivityFrame.Title = activityTitle;

        static void SetTitle(FrameView frame, string baseTitle, OwnerConsoleOverflow overflow)
        {
            var title = OwnerConsoleOverflow.Title(baseTitle, overflow, frame.Frame.Width - OwnerConsoleOverflow.TitleChrome);
            if (frame.Title != title) frame.Title = title;
        }
    }

    internal string PaneTitle(OwnerConsolePane pane) => pane switch
    {
        OwnerConsolePane.Decisions => _layout.DecisionsFrame.Title,
        OwnerConsolePane.Board => _layout.BoardFrame.Title,
        OwnerConsolePane.Activity => _layout.ActivityFrame.Title,
        _ => throw new ArgumentOutOfRangeException(nameof(pane))
    };

    internal void SetWorking(string source, string? label)
    {
        if (label is null) _working.Remove(source);
        else _working[source] = label;
        RenderStatus();
    }

    public void ShowNotice(string message, OwnerConsoleNoticeSeverity severity, OwnerConsoleNoticeSource source)
    {
        NoticeStrip.Raise(message, severity, source);
        RenderNotices();
    }

    private void RenderNotices()
    {
        if (_token.IsCancellationRequested) return;
        _notice.Text = NoticeStrip.Format(_notice.Viewport.Width);
        // An uninitialized real application has no UI dispatcher. Injected clocks let
        // headless callers drive callbacks themselves rather than mutating UI on a timer thread.
        if (!_app.Initialized && ReferenceEquals(_clock, TimeProvider.System)) return;
        _noticeExpiry.Rearm(NoticeStrip.UntilNextSuccessExpiry);
    }

    private void RenderStatus()
    {
        if (_controller.Model is not { } model) return;
        _status.Text = _statusPresenter.Compose(model.Status, model.Gate, _controller.BellStatusText,
            _refreshedAt, _clock.GetUtcNow(), _clock.LocalTimeZone, _refreshInterval, _working.Values, _status.Viewport.Width);
    }

    // Host ticks repaint freshness even when the state refresh is blocked or has failed.
    internal void RefreshStatus() { RenderStatus(); RenderNotices(); }

    private void RenderActivity()
    {
        using var trace = _diagnostics?.Begin("activity-source-replacement", TraceState);
        _activityPane.Render(_controller.Model, _activityWidth);
    }
    private int? ActivityRow() => _activityPane.SelectedRow(_controller.Model);
    public void FollowLatest()
    {
        _rendering = true;
        try
        {
            using var trace = _diagnostics?.Begin("activity-source-replacement", TraceState);
            _activityPane.FollowLatest(_controller.Model, _activityWidth);
        }
        finally { _rendering = false; }
        RenderHints();
        RenderOverflow();
    }

    private async void OnKeyDown(object? sender, Key key)
    {
        if (_diagnostics is null && _app.TopRunnableView != Window) return;
        try
        {
            await (_diagnostics is null ? HandleKeyCoreAsync(key) :
                ObserveKeyAsync(key, _app.TopRunnableView == Window, "keyboard-callback"));
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception ex) { ShowNotice($"key action failed: {ex.Message}", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action); }
    }

    // The production callback and headless tests share routing; their input boundaries remain distinct.
    internal Task HandleKeyAsync(Key key) => _diagnostics is null ? HandleKeyCoreAsync(key) :
        ObserveKeyAsync(key, true, "headless-input");

    internal async Task ObserveKeyAsync(Key key, bool routeToWindow, string boundary)
    {
        using var input = _diagnostics?.Begin(boundary, TraceState, key.ToString(), input: true);
        if (!routeToWindow)
        {
            if (input is not null) input.Outcome = "excluded-top-window";
            return;
        }
        using var handler = _diagnostics?.Begin("handler", TraceState);
        try
        {
            await HandleKeyCoreAsync(key);
            if (handler is not null) handler.Outcome = key.Handled ? "handled" : "unhandled";
            if (input is not null) input.Outcome = key.Handled ? "handled" : "unhandled";
        }
        catch (Exception ex)
        {
            if (handler is not null) handler.Outcome = ex is OperationCanceledException ? "cancelled" : "faulted";
            if (input is not null) input.Outcome = handler?.Outcome ?? "faulted";
            throw;
        }
    }

    private Task HandleKeyCoreAsync(Key key) => _router.RouteAsync(key);

    public Task RunBindingAsync(OwnerConsoleBinding binding, string? argument = null, bool fromKey = false) => _bindingRunner.RunAsync(binding, argument, fromKey);

    public void OpenGate(string? goalId = null)
    {
        var gate = _controller.Model?.Gate;
        if (goalId is not null && gate?.ForMember(goalId) is null)
            ShowNotice($"gate {goalId}: not in a live gate", OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        else GateView.Open(FocusedPane, gate, goalId);
    }

    public async Task ActAsync(Func<CancellationToken, Task> action)
    {
        // Modal waits belong to the owner; only the controller's dependency work is bounded.
        StartAction();
        _acting = true;
        try
        {
            await action(_token);
            if (!_token.IsCancellationRequested) await _refresh();
        }
        finally { _acting = false; }
        if (_controller.TakeRequestedEpicId() is { } epicId && !_token.IsCancellationRequested)
            await EpicView.OpenAsync(FocusedPane, epicId);
        if (_controller.TakeRequestedGateGoalId() is { } gateGoalId && !_token.IsCancellationRequested)
            OpenGate(gateGoalId);
    }

    public void StartAction() { NoticeStrip.ActionStarted(); RenderNotices(); }

    internal void FocusDecisions() => FocusPane(OwnerConsolePane.Decisions);

    public View PaneView(OwnerConsolePane pane) => pane switch
    {
        OwnerConsolePane.Decisions => _decisions,
        OwnerConsolePane.Board => _board,
        OwnerConsolePane.Activity => _activity,
        _ => throw new ArgumentOutOfRangeException(nameof(pane))
    };

    public void FocusPane(OwnerConsolePane pane)
    {
        _focusedPane = pane;
        PaneView(pane).SetFocus();
        RenderHints();
    }

    private void PaneFocusChanged(View view, OwnerConsolePane pane)
    {
        if (view.HasFocus) _focusedPane = pane;
        RenderHints();
    }

    public void RenderHints() => _hints.Text = _editingCommand || _command.HasFocus
        ? _completionHint ?? OwnerConsoleKeyHints.CommandLineHint
        : (FocusedPane == OwnerConsolePane.Board && OwnerConsoleBoardCollapse.Enabled(_controller.Model)
            ? OwnerConsoleBoardCollapse.KeyHint + "  " : "") + OwnerConsoleKeyHints.Hint(FocusedPane,
            _controller.SelectedDecision is { } decision && OwnerConsoleScreenController.AnswersInConsole(decision), _activityPane.Following);

    public void SelectBoard(int index, bool refresh = false, int direction = 0, bool allowHeader = false)
    {
        if (!refresh) _copySelectionValid = true;
        using var trace = _diagnostics?.Begin("select-board", TraceState);
        if (_controller.Model is not { Board.Length: > 0 } model)
        {
            _selectedBoardIndex = -1; SelectedGoalId = null; SelectedProject = null;
            if (!_previewPane.Select(null)) RenderPreview();
            return;
        }
        var selectable = allowHeader && index >= 0 && index < BoardTable.Rows.Count && model.Board[index].IsDiagnostic
            ? index : OwnerConsoleBoardSection.NearestGoal(model.Board, model.BoardScope, index, direction);
        _selectedBoardIndex = selectable < 0 ? 0 : selectable;
        var row = model.Board[_selectedBoardIndex];
        SelectedGoalId = row.IsDiagnostic ? null : row.GoalId;
        SelectedProject = selectable < 0 ? null : row.Project;
        if (!_previewPane.Select(row.IsForeign || row.IsDiagnostic ? null : SelectedGoalId, refresh))
            RenderPreview();
        if (selectable < 0 || BoardTable.Rows.Count == 0) return;
        if (_board.Value?.SelectedCell.Y != _selectedBoardIndex)
            _board.SetSelection(Math.Max(0, OwnerConsoleBoardRenderer.FieldKeys.ToList().IndexOf(_selectedFieldKey ?? "")), _selectedBoardIndex, false);
        _board.EnsureCursorIsVisible();
        RenderOverflow();
    }

    private void RenderPreview() => _previewPane.Render();

    public bool RefuseSelectedBoardRow()
    {
        if (FocusedPane != OwnerConsolePane.Board || SelectedBoardRow is not { } row ||
            !row.IsForeign && !row.IsDiagnostic) return false;
        StartAction();
        ShowNotice(row.IsDiagnostic ? row.Title : OwnerConsoleBoardSource.ForeignRowMessage(row),
            OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action);
        return true;
    }

    private void Invoke(Action action)
    {
        if (_app.Initialized && _diagnostics is not null)
        {
            _app.Invoke(_diagnostics.ObservePost("view-invoke", action, TraceState));
        }
        else if (_app.Initialized) _app.Invoke(action);
        else action();
    }

    private void OnLayoutAndDrawComplete(object? sender, EventArgs args) =>
        _diagnostics?.Mark("layout-draw-complete", TraceState);

    private OwnerConsoleTraceState TraceState()
    {
        static OwnerConsoleTracePane Pane(View view, int index, string? identity, int count) =>
            new(index, identity, count, view.Viewport.X, view.Viewport.Y, view.Viewport.Width, view.Viewport.Height);
        return new(_editingCommand || _command.HasFocus ? "Command" : FocusedPane.ToString(),
            _editingCommand, EpicView.IsOpen || GateView.IsOpen || (_app.Initialized && _app.TopRunnableView != Window),
            Pane(_decisions, _decisions.SelectedItem ?? -1, _controller.SelectedDecisionId, _controller.Model?.Decisions.Length ?? 0),
            Pane(_board, _selectedBoardIndex, SelectedGoalId, _controller.Model?.Board.Length ?? 0) with
                { RowOffset = _board.RowOffset, ColumnOffset = _board.ColumnOffset },
            Pane(_activity, _activity.SelectedItem ?? -1, null, ActivityLines.Count));
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _previewPane.Dispose();
        _noticeExpiry.Dispose();
        _controller.Dispose();
        if (_keyboard is not null) _keyboard.KeyDown -= OnKeyDown;
        if (_diagnostics is not null) _app.LayoutAndDrawComplete -= OnLayoutAndDrawComplete;
        Window.Dispose();
        BoardTable.Dispose();
        _lifetime.Dispose();
    }
}
