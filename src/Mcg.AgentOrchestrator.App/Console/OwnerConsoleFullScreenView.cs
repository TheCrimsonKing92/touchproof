using System.Data;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleFullScreenView : IDisposable, IOwnerConsoleKeySurface, IOwnerConsoleBindingSurface
{
    private readonly OwnerConsoleScreenController _controller;
    private readonly OwnerConsoleKeyObserver _keys;
    private readonly OwnerConsoleViewTrace _trace;
    private readonly OwnerConsoleBoardSelection _selection;
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
    public OwnerConsoleFieldSnapshot? SelectedBoardFields => _selection.Fields;
    public ListView DecisionsPane => _decisions;
    public ListView ActivityPane => _activity;
    internal string HintText => _hints.Text;
    internal IReadOnlyList<string> DecisionLines => _decisionsPane.Lines;
    public IReadOnlyList<string> ActivityLines => _activityPane.Lines;
    public string? SelectedGoalId => _selection.GoalId;
    public string? SelectedProject => _selection.Project;
    private OwnerConsoleBoardRow? SelectedBoardRow => _selection.Row;
    public OwnerConsolePane FocusedPane => _decisions.HasFocus ? OwnerConsolePane.Decisions :
        _board.HasFocus ? OwnerConsolePane.Board : _activity.HasFocus ? OwnerConsolePane.Activity : _focusedPane;

    public bool EditingCommand { get => _editingCommand; set => _editingCommand = value; }
    string? IOwnerConsoleKeySurface.CompletionHint { set => _completionHint = value; }
    public View? CommandReturnFocus { get => _commandReturnFocus; set => _commandReturnFocus = value; }
    int IOwnerConsoleKeySurface.HintWidth => _hints.Viewport.Width;
    int IOwnerConsoleKeySurface.SelectedBoardIndex => _selection.Index;
    string? IOwnerConsoleKeySurface.SelectedFieldKey { get => _selectedFieldKey; set => _selectedFieldKey = value; }
    bool IOwnerConsoleKeySurface.CopySelectionValid { set => _selection.CopyValid = value; }
    OwnerConsoleViewModel? IOwnerConsoleKeySurface.ArrangedBoard => _arrangedBoard;
    public bool Rendering { set => _rendering = value; }
    public OwnerConsoleActivityPane ActivityModel => _activityPane;
    OwnerConsoleFieldSnapshot? IOwnerConsoleKeySurface.DecisionFields => _decisionsPane.SelectedFields;
    int IOwnerConsoleBindingSurface.ActivityWidth => _activityWidth;
    IDisposable? IOwnerConsoleBindingSurface.BeginActivitySourceReplacement() => _trace.BeginActivitySourceReplacement();
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
        _trace = new(this, app, Window, controller, diagnostics);
        _selection = new(this, controller, _previewPane, _board, BoardTable, diagnostics, _trace.State, () => _selectedFieldKey);
        _board.Table = new DataTableSource(BoardTable);
        _epicHint.Width = OwnerConsoleEpicFormatter.KeyHint.Length;
        _epicHint.X = Pos.AnchorEnd(OwnerConsoleEpicFormatter.KeyHint.Length);
        _hints.Width = Dim.Fill(OwnerConsoleEpicFormatter.KeyHint.Length + 2);
        EpicView = new(controller, Invoke, token);
        _layout.Compose(Window, _status, _notice, _hints, _epicHint, _command, EpicView, GateView);
        new OwnerConsoleFullScreenWiring(
            _previewPane.Body, _notice, _status, _decisions, _board, _activity, _command, _activityPane,
            _controller, _diagnostics, _trace.State,
            () => _rendering, value => _rendering = value, value => _selection.CopyValid = value,
            value => _selectedFieldKey = value, () => _completionHint, value => _completionHint = value,
            () => _activityWidth, () => _boardTitleWidth,
            RenderPreview, RenderNotices, RenderStatus, RenderHints, RenderOverflow, RenderDecisions,
            index => SelectBoard(index), PaneFocusChanged, Fit, ActivityRow).Attach();
        RenderHints();
        _keys = new(this, app, Window, new(this, _controller, _operation, _clipboard, _collapse, _token),
            diagnostics, _trace.State, _token);
        _bindingRunner = new(this, _controller, _operation, _boardSource is null ? null : _boardSource.HandleScope, _refresh, _token);
        _keyboard = app.Initialized ? app.Keyboard : null;
        if (_keyboard is not null) _keyboard.KeyDown += _keys.OnKeyDown;
        if (_diagnostics is not null) _app.LayoutAndDrawComplete += _trace.OnLayoutAndDrawComplete;
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
        using var trace = _diagnostics?.Begin("render", _trace.State);
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
            if (SelectedGoalId is not null && index < 0) _selection.CopyValid = false;
            SelectBoard(index < 0 ? _selection.Index : index, refresh: true,
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
        using var trace = _diagnostics?.Begin("fit", _trace.State);
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
        OwnerConsoleBoardRenderer.Render(BoardTable, _board, model.Board, _board.Viewport.Width, _boardTitleWidth,
            model.BoardScope is { Sections: not null });
        var fieldColumn = OwnerConsoleBoardRenderer.FieldKeys.ToList().IndexOf(_selectedFieldKey ?? "");
        if (fieldColumn < 0 || !_board.Style.GetOrCreateColumnStyle(fieldColumn).Visible) _selectedFieldKey = null;
        if (fieldColumn >= 0 && _selection.Index >= 0 && _selection.Index < BoardTable.Rows.Count)
            _board.SetSelection(fieldColumn, _selection.Index, false);
        RenderPreview();
    }

    private void RenderDecisions()
    {
        using var trace = _diagnostics?.Begin("decisions-source-replacement", _trace.State);
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
        using var trace = _diagnostics?.Begin("activity-source-replacement", _trace.State);
        _activityPane.Render(_controller.Model, _activityWidth);
    }
    private int? ActivityRow() => _activityPane.SelectedRow(_controller.Model);
    public void FollowLatest()
    {
        _rendering = true;
        try
        {
            using var trace = _diagnostics?.Begin("activity-source-replacement", _trace.State);
            _activityPane.FollowLatest(_controller.Model, _activityWidth);
        }
        finally { _rendering = false; }
        RenderHints();
        RenderOverflow();
    }

    internal Task HandleKeyAsync(Key key) => _keys.HandleKeyAsync(key);

    internal Task ObserveKeyAsync(Key key, bool routeToWindow, string boundary) => _keys.ObserveKeyAsync(key, routeToWindow, boundary);

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

    public void SelectBoard(int index, bool refresh = false, int direction = 0, bool allowHeader = false) =>
        _selection.Select(index, refresh, direction, allowHeader);

    private void RenderPreview() => _previewPane.Render();

    public bool RefuseSelectedBoardRow() => _selection.Refuse();

    private void Invoke(Action action)
    {
        if (_app.Initialized && _diagnostics is not null)
        {
            _app.Invoke(_diagnostics.ObservePost("view-invoke", action, _trace.State));
        }
        else if (_app.Initialized) _app.Invoke(action);
        else action();
    }

    public void Dispose()
    {
        _lifetime.Cancel();
        _previewPane.Dispose();
        _noticeExpiry.Dispose();
        _controller.Dispose();
        if (_keyboard is not null) _keyboard.KeyDown -= _keys.OnKeyDown;
        if (_diagnostics is not null) _app.LayoutAndDrawComplete -= _trace.OnLayoutAndDrawComplete;
        Window.Dispose();
        BoardTable.Dispose();
        _lifetime.Dispose();
    }
}
