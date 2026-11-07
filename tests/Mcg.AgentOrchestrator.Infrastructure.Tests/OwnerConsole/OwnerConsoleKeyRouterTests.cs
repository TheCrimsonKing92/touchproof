using System.Data;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Input;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Parallel-safe: private widgets, in-memory controller dependencies, no terminal driver or global state.
public sealed class OwnerConsoleKeyRouterTests
{
    [Fact]
    public async Task GateEscape_WhileEditing_ClosesGateAndRestoresItsPaneFirst()
    {
        using var surface = new Surface { EditingCommand = true };
        surface.CommandLine.Text = ":he";
        surface.GateView.Open(OwnerConsolePane.Board, OwnerConsoleGateProgress.Idle());
        Assert.True(surface.GateView.IsOpen);
        var key = Key.Esc;

        await surface.Router.RouteAsync(key);

        Assert.True(key.Handled);
        Assert.False(surface.GateView.IsOpen);
        Assert.Equal(OwnerConsolePane.Board, surface.FocusedPane);
        Assert.True(surface.EditingCommand);
        Assert.Equal(":he", surface.CommandLine.Text);
        Assert.Equal(new[] {"FocusPane:Board" }, surface.Calls);
        Assert.Empty(surface.Dialogs.Messages);
    }

    [Fact]
    public async Task Tab_WhileEditing_CompletesCommandWithoutCyclingPanes()
    {
        using var surface = new Surface { EditingCommand = true };
        surface.CommandLine.Text = ":he";
        var completion = OwnerConsoleCommandCompletion.Complete(":he", OwnerConsoleBindings.Commands, surface.HintWidth);
        var key = Key.Tab;

        await surface.Router.RouteAsync(key);

        Assert.True(key.Handled);
        Assert.Equal(":help", surface.CommandLine.Text);
        Assert.Equal(completion.Text, surface.CommandLine.Text);
        Assert.Equal(surface.CommandLine.Text.Length, surface.CommandLine.InsertionPoint);
        Assert.Equal(completion.Hint, surface.CompletionHint);
        Assert.True(surface.EditingCommand);
        Assert.Equal(OwnerConsolePane.Decisions, surface.FocusedPane);
        Assert.Equal(new[] {"RenderHints" }, surface.Calls);
    }

    [Fact]
    public async Task Tab_FromDecisions_FocusesBoard()
    {
        using var surface = new Surface();
        var key = Key.Tab;

        await surface.Router.RouteAsync(key);

        Assert.True(key.Handled);
        Assert.Equal(OwnerConsolePane.Board, surface.FocusedPane);
        Assert.Equal(new[] {"FocusPane:Board" }, surface.Calls);
    }

    [Fact]
    public async Task PageDown_OnBoard_SelectsOneVisiblePageThenRendersHintsAndOverflow()
    {
        using var surface = new Surface { FocusedPane = OwnerConsolePane.Board };
        for (var index = 0; index < 12; index++) surface.BoardTable.Rows.Add(index);
        var key = Key.PageDown;

        await surface.Router.RouteAsync(key);

        Assert.True(key.Handled);
        Assert.Equal(5, surface.SelectedBoardIndex);
        Assert.Equal(new[] {"SelectBoard:5:False:1:False", "RenderHints", "RenderOverflow" }, surface.Calls);
    }

    [Fact]
    public async Task Enter_OnSelectedDecision_RunsActionAndShowsThatQuestion()
    {
        using var surface = new Surface();
        await surface.AddDecisionAsync();
        Assert.Equal(0, surface.Controller.SelectedIndex);
        var key = Key.Enter;

        await surface.Router.RouteAsync(key);

        Assert.True(key.Handled);
        Assert.Equal(new[] {"ActAsync" }, surface.Calls);
        Assert.Contains("Ship this goal?", Assert.Single(surface.Dialogs.Messages).Text);
    }

    [Fact]
    public async Task PrintableUnboundKey_ShowsFailureNoticeAfterStartingAction()
    {
        using var surface = new Surface();
        const char character = 'x';
        Assert.Null(OwnerConsoleBindings.ForKey(character, surface.FocusedPane));
        Assert.False(OwnerConsoleBindings.IsTopLevelKey(character));
        var key = new Key(character);

        await surface.Router.RouteAsync(key);

        Assert.False(key.Handled);
        Assert.Equal(new[] {"StartAction", "ShowNotice" }, surface.Calls);
        Assert.Equal(("x: not bound; press : for commands or ? for help", OwnerConsoleNoticeSeverity.Failure,
            OwnerConsoleNoticeSource.Action), Assert.Single(surface.Notices));
        Assert.Empty(surface.Dialogs.Messages);
    }

    private sealed class Surface : IOwnerConsoleKeySurface, IDisposable
    {
        private readonly OwnerConsoleHarness _harness = new();
        internal RecordingDialogs Dialogs { get; } = new();
        internal OwnerConsoleScreenController Controller { get; }
        internal OwnerConsoleKeyRouter Router { get; }
        internal List<string> Calls { get; } = [];
        internal List<(string, OwnerConsoleNoticeSeverity, OwnerConsoleNoticeSource)> Notices { get; } = [];
        public OwnerConsoleGateDialog GateView { get; } = new();
        public OwnerConsoleEpicDialog EpicView { get; }
        public TextField CommandLine { get; } = new();
        public bool EditingCommand { get; set; }
        public string? CompletionHint { get; set; }
        public View? CommandReturnFocus { get; set; }
        public int HintWidth => 80;
        public OwnerConsolePane FocusedPane { get; set; } = OwnerConsolePane.Decisions;
        public TableView BoardPane { get; } = new();
        public ListView DecisionsPane { get; } = new();
        public ListView ActivityPane => ActivityModel.List;
        public DataTable BoardTable { get; } = new();
        public int SelectedBoardIndex { get; private set; }
        public OwnerConsoleFieldSnapshot? SelectedBoardFields => null;
        public string? SelectedFieldKey { get; set; }
        public bool CopySelectionValid { private get; set; }
        public string? SelectedGoalId => null;
        public string? SelectedProject => null;
        public OwnerConsoleViewModel? ArrangedBoard => null;
        public bool Rendering { private get; set; }
        public IReadOnlyList<string> ActivityLines => ActivityModel.Lines;
        public OwnerConsoleActivityPane ActivityModel { get; } = new();
        public OwnerConsoleFieldSnapshot? DecisionFields => null;
        public bool ActionRunning => false;

        internal Surface()
        {
            Controller = OwnerConsoleControllerFactory.Create(_harness.Questions, _harness.Answers, Dialogs,
                _harness.State, _harness.Tail, _harness.Conductor, _harness.DigestReport, _harness.Digest, _harness.Clock);
            EpicView = new(Controller, action => action(), CancellationToken.None);
            var operation = new OwnerConsoleScreenOperation(_harness.Clock, _ => { }, _ => { });
            Router = new(this, Controller, operation, new(_ => throw new InvalidOperationException("Unexpected copy")),
                new(), CancellationToken.None);
            BoardTable.Columns.Add("row", typeof(int));
        }

        internal async Task AddDecisionAsync()
        {
            _harness.AddGoal("11111111", "Ship", AgentRole.Developer);
            _harness.Questions.Items.Add(new("q1", "11111111", OwnerQuestionKind.HumanInput, "Ship this goal?"));
            var builder = new OwnerConsoleViewModelBuilder(_harness.State, _harness.Questions, _harness.Liveness,
                new Epics(), _harness.Clock);
            Controller.Apply(await builder.BuildAsync(new(_harness.Clock.GetUtcNow(), null, [], 0)));
        }

        public View PaneView(OwnerConsolePane pane) => pane switch
        {
            OwnerConsolePane.Decisions => DecisionsPane,
            OwnerConsolePane.Board => BoardPane,
            OwnerConsolePane.Activity => ActivityPane,
            _ => throw new ArgumentOutOfRangeException(nameof(pane))
        };
        public void FocusPane(OwnerConsolePane pane) { Calls.Add("FocusPane:" + pane); FocusedPane = pane; }
        public void RenderBoard() => throw new InvalidOperationException("Unexpected board render");
        public void SelectBoard(int index, bool refresh = false, int direction = 0, bool allowHeader = false)
        {
            Calls.Add($"SelectBoard:{index}:{refresh}:{direction}:{allowHeader}");
            SelectedBoardIndex = index;
        }
        public int BoardBodyRows() => 5;
        public bool RefuseSelectedBoardRow() => throw new InvalidOperationException("Unexpected board action");
        public void FollowLatest() => throw new InvalidOperationException("Unexpected follow latest");
        public void ShowNotice(string message, OwnerConsoleNoticeSeverity severity, OwnerConsoleNoticeSource source)
        { Calls.Add("ShowNotice"); Notices.Add((message, severity, source)); }
        public void StartAction() => Calls.Add("StartAction");
        public void RenderHints() => Calls.Add("RenderHints");
        public void RenderOverflow() => Calls.Add("RenderOverflow");
        public Task ActAsync(Func<CancellationToken, Task> action)
        { Calls.Add("ActAsync"); return action(CancellationToken.None); }
        public Task RunBindingAsync(OwnerConsoleBinding binding, string? argument = null, bool fromKey = false) =>
            throw new InvalidOperationException("Unexpected key binding");
        public void Dispose()
        {
            EpicView.Dispose(); GateView.Dispose(); CommandLine.Dispose(); BoardPane.Dispose();
            DecisionsPane.Dispose(); ActivityPane.Dispose(); BoardTable.Dispose(); Controller.Dispose();
        }
    }

    private sealed class RecordingDialogs : IOwnerConsoleDialogs
    {
        internal List<(string Title, string Text)> Messages { get; } = [];
        public Task ShowTextAsync(string title, string text) { Messages.Add((title, text)); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected prompt");
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
