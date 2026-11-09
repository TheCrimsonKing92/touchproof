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
    private readonly OwnerConsoleRenderCoordinator _renderCoordinator;
    private readonly OwnerConsoleRenderState _state = new();
    private readonly OwnerConsolePaneRenderer _paneRenderer;
    private readonly OwnerConsolePreviewPane _previewPane;
    internal string PreviewText => _previewPane.Body.Text;
    internal Task PreviewLoad => _previewPane.LastLoad;
    private readonly ListView _activity;
    private readonly TextField _command = new() { Y = Pos.AnchorEnd(1), Width = Dim.Fill(), Height = 1 };
    private readonly Label _hints = new() { Y = Pos.AnchorEnd(2), Width = Dim.Fill(), Height = 1 };
    private readonly Label _epicHint = new() { Y = Pos.AnchorEnd(2), Text = OwnerConsoleEpicFormatter.KeyHint, Height = 1 };
    public OwnerConsoleEpicDialog EpicView { get; }
    public OwnerConsoleGateDialog GateView { get; } = new();
    public OwnerConsoleSettingsView SettingsView { get; }
    internal string EpicHintText => _epicHint.Text;
    private readonly Dictionary<string, string> _working = new();
    private readonly OwnerConsoleScreenOperation _operation;
    private bool _editingCommand;
    private string? _completionHint;
    private bool _acting;
    private OwnerConsolePane _focusedPane = OwnerConsolePane.Decisions;
    private readonly OwnerConsoleBoardCollapse _collapse = new();
    internal bool ActivityFollowing => _activityPane.Following;
    internal string? ActivityFilter => _activityPane.Filter;
    private View? _commandReturnFocus;
    public bool ActionRunning => _acting || _operation.IsRunning;

    internal Window Window { get; } = new() { Title = "Owner console", Width = Dim.Fill(), Height = Dim.Fill() };
    public DataTable BoardTable { get; } = OwnerConsoleFullScreenComposer.CreateBoardTable();
    internal string StatusText => _status.Text;
    internal OwnerConsoleNoticeStrip NoticeStrip { get; }
    internal string NoticeText => _notice.Text;
    public TextField CommandLine => _command;
    public TableView BoardPane => _board;
    public OwnerConsoleFieldSnapshot? SelectedBoardFields => _selection.Fields;
    public ListView DecisionsPane => _decisions;
    public ListView ActivityPane => _activity;
    internal string HintText => _hints.Text;
    internal IReadOnlyList<string> DecisionLines => _decisionsPane.Lines;
    public IReadOnlyList<string> ActivityLines => _activityPane.Lines;
    public string? SelectedGoalId => _selection.GoalId;
    public string? SelectedProject => _selection.Project;
    internal OwnerConsoleBoardRow? SelectedBoardRow => _selection.Row;
    public OwnerConsolePane FocusedPane => _decisions.HasFocus ? OwnerConsolePane.Decisions :
        _board.HasFocus ? OwnerConsolePane.Board : _activity.HasFocus ? OwnerConsolePane.Activity : _focusedPane;

    public bool EditingCommand { get => _editingCommand; set => _editingCommand = value; }
    string? IOwnerConsoleKeySurface.CompletionHint { set => _completionHint = value; }
    public View? CommandReturnFocus { get => _commandReturnFocus; set => _commandReturnFocus = value; }
    int IOwnerConsoleKeySurface.HintWidth => _hints.Viewport.Width;
    int IOwnerConsoleKeySurface.SelectedBoardIndex => _selection.Index;
    string? IOwnerConsoleKeySurface.SelectedFieldKey { get => _state.SelectedFieldKey; set => _state.SelectedFieldKey = value; }
    bool IOwnerConsoleKeySurface.CopySelectionValid { set => _selection.CopyValid = value; }
    OwnerConsoleViewModel? IOwnerConsoleKeySurface.ArrangedBoard => _state.ArrangedBoard;
    public bool Rendering { set => _state.Rendering = value; }
    public OwnerConsoleActivityPane ActivityModel => _activityPane;
    OwnerConsoleFieldSnapshot? IOwnerConsoleKeySurface.DecisionFields => _decisionsPane.SelectedFields;
    int IOwnerConsoleBindingSurface.ActivityWidth => _state.ActivityWidth;
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
        _diagnostics = diagnostics;
        _controller = controller;
        _refresh = refresh;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
        _token = _lifetime.Token;
        (NoticeStrip, _noticeExpiry, _operation, _layout, _renderCoordinator, _previewPane, _trace, _selection, _paneRenderer, EpicView, SettingsView) =
            OwnerConsoleFullScreenComposer.Build(this, app, controller, token, _token, clock ?? TimeProvider.System, options, diagnostics,
                _decisionsPane, _activityPane, _board, _notice, _status, _hints, _epicHint, _statusPresenter, _state, _working, _collapse);
        (_keys, _bindingRunner, _keyboard) = OwnerConsoleFullScreenComposer.Attach(this, app, controller, refresh, _token,
            diagnostics, boardSource, clipboard, _layout, _previewPane, _trace, _selection, _paneRenderer, _operation,
            _activityPane, _board, _notice, _status, _hints, _epicHint, _state, _collapse, () => _completionHint, value => _completionHint = value);
    }

    internal void Render(OwnerConsoleViewModel model)
    {
        _paneRenderer.Render(model);
        SettingsView.Refresh();
    }

    internal void Fit(int activityWidth, int boardTitleWidth) => _paneRenderer.Fit(activityWidth, boardTitleWidth);

    public void RenderBoard() => _paneRenderer.RenderBoard();

    public int BoardBodyRows() => _renderCoordinator.BoardBodyRows();
    public void RenderOverflow() => _renderCoordinator.RenderOverflow();

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

    public void ShowNotice(string message, OwnerConsoleNoticeSeverity severity, OwnerConsoleNoticeSource source) => _renderCoordinator.ShowNotice(message, severity, source);
    internal void RenderNotices() => _renderCoordinator.RenderNotices();
    internal void RenderStatus() => _renderCoordinator.RenderStatus();
    // Host ticks repaint freshness even when the state refresh is blocked or has failed.
    internal void RefreshStatus() => _renderCoordinator.RefreshStatus();

    internal int? ActivityRow() => _activityPane.SelectedRow(_controller.Model);
    public void FollowLatest()
    {
        _state.Rendering = true;
        try
        {
            using var trace = _diagnostics?.Begin("activity-source-replacement", _trace.State);
            _activityPane.FollowLatest(_controller.Model, _state.ActivityWidth);
        }
        finally { _state.Rendering = false; }
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
        if (_controller.TakeSettingsRequested() && !_token.IsCancellationRequested)
            SettingsView.Open(FocusedPane);
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

    internal void PaneFocusChanged(View view, OwnerConsolePane pane)
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

    internal void RenderPreview() => _previewPane.Render();

    public bool RefuseSelectedBoardRow() => _selection.Refuse();

    internal void Invoke(Action action)
    {
        if (!_app.Initialized) action();
        else _app.Invoke(_diagnostics?.ObservePost("view-invoke", action, _trace.State) ?? action);
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
