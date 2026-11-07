using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.ViewBase;
using Terminal.Gui.Views;

// Parallel-safe: private widgets and in-memory controller dependencies, without a terminal driver.
public sealed class OwnerConsoleBindingRunnerTests
{
    [Fact]
    public async Task Quit_WhileActionRunning_RequestsStop()
    {
        using var surface = new Surface { ActionRunning = true };

        await surface.Runner.RunAsync(OwnerConsoleBindings.Quit);

        Assert.True(surface.Controller.QuitRequested);
        Assert.Equal(new[] { "RequestStop" }, surface.Calls);
        Assert.Empty(surface.Notices);
    }

    [Fact]
    public async Task ControllerCommand_RunsThroughAction()
    {
        using var surface = new Surface();
        var binding = OwnerConsoleBindings.ForLine("digest")!;
        Assert.Equal(OwnerConsoleCommandOwner.Controller, binding.Owner);

        await surface.Runner.RunAsync(binding);

        Assert.Equal(new[] { "ActAsync" }, surface.Calls);
        var message = Assert.Single(surface.Dialogs.Messages);
        Assert.Equal("Command result", message.Title);
        Assert.Equal("Owner digest: landed=2 pending=1" + Environment.NewLine, message.Text);
        Assert.Empty(surface.Notices);
    }

    [Fact]
    public async Task DecisionKey_OutsideDecisions_ShowsUnavailableNotice()
    {
        using var surface = new Surface { FocusedPane = OwnerConsolePane.Activity };
        var binding = OwnerConsoleBindings.ForKey('a', OwnerConsolePane.Decisions)!;
        Assert.Equal(OwnerConsoleScope.Decisions, binding.Scope);

        await surface.Runner.RunAsync(binding, fromKey: true);

        Assert.Equal(new[] { "RefuseSelectedBoardRow", "StartAction", "ShowNotice" }, surface.Calls);
        Assert.Equal((OwnerConsoleKeyHints.UnavailableKey('a', OwnerConsolePane.Activity),
            OwnerConsoleNoticeSeverity.Failure, OwnerConsoleNoticeSource.Action), Assert.Single(surface.Notices));
        Assert.Empty(surface.Dialogs.Messages);
    }

    [Fact]
    public async Task FilterKey_OpensCommandLineWithCurrentFilter()
    {
        using var surface = new Surface();
        surface.ActivityModel.Refilter(null, "goal123", surface.ActivityWidth);
        Assert.Equal("goal123", surface.ActivityModel.Filter);

        await surface.Runner.RunAsync(OwnerConsoleBindings.Filter, fromKey: true);

        Assert.True(surface.EditingCommand);
        Assert.Same(surface.ActivityPane, surface.CommandReturnFocus);
        Assert.Equal("filter goal123", surface.CommandLine.Text);
        Assert.Equal(surface.CommandLine.Text.Length, surface.CommandLine.InsertionPoint);
        Assert.Equal(new[] { "RenderHints" }, surface.Calls);
        Assert.Empty(surface.Notices);
    }

    [Fact]
    public async Task UnknownBinding_Throws()
    {
        using var surface = new Surface();
        var binding = new OwnerConsoleBinding("unknown", "unknown", null, OwnerConsoleScope.Global,
            OwnerConsoleEffect.View, OwnerConsoleCommandOwner.View, false, "unknown", null, null, "Unknown");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => surface.Runner.RunAsync(binding));

        Assert.Equal("No view action for binding unknown.", error.Message);
        Assert.Empty(surface.Calls);
        Assert.Empty(surface.Notices);
    }

    private sealed class Surface : IOwnerConsoleBindingSurface, IDisposable
    {
        private readonly OwnerConsoleHarness _harness = new();
        internal RecordingDialogs Dialogs { get; } = new();
        internal OwnerConsoleScreenController Controller { get; }
        internal OwnerConsoleBindingRunner Runner { get; }
        internal List<string> Calls { get; } = [];
        internal List<(string, OwnerConsoleNoticeSeverity, OwnerConsoleNoticeSource)> Notices { get; } = [];
        public bool ActionRunning { get; set; }
        public OwnerConsolePane FocusedPane { get; set; } = OwnerConsolePane.Decisions;
        public TextField CommandLine { get; } = new();
        public OwnerConsoleActivityPane ActivityModel { get; } = new();
        public ListView ActivityPane => ActivityModel.List;
        public OwnerConsoleEpicDialog EpicView { get; }
        public bool EditingCommand { get; set; }
        public View? CommandReturnFocus { get; set; }
        public bool Rendering { private get; set; }
        public int ActivityWidth => 80;

        internal Surface()
        {
            Controller = OwnerConsoleControllerFactory.Create(_harness.Questions, _harness.Answers, Dialogs,
                _harness.State, _harness.Tail, _harness.Conductor, _harness.DigestReport, _harness.Digest, _harness.Clock);
            EpicView = new(Controller, action => action(), CancellationToken.None);
            var operation = new OwnerConsoleScreenOperation(_harness.Clock, _ => { }, _ => { });
            Runner = new(this, Controller, operation, null,
                () => throw new InvalidOperationException("Unexpected refresh"), CancellationToken.None);
        }

        public bool RefuseSelectedBoardRow() { Calls.Add("RefuseSelectedBoardRow"); return false; }
        public void StartAction() => Calls.Add("StartAction");
        public void ShowNotice(string message, OwnerConsoleNoticeSeverity severity, OwnerConsoleNoticeSource source)
        { Calls.Add("ShowNotice"); Notices.Add((message, severity, source)); }
        public Task ActAsync(Func<CancellationToken, Task> action)
        { Calls.Add("ActAsync"); return action(CancellationToken.None); }
        public void OpenGate(string? goalId) => throw new InvalidOperationException("Unexpected gate");
        public void FocusPane(OwnerConsolePane pane) => throw new InvalidOperationException("Unexpected focus");
        public void FollowLatest() => throw new InvalidOperationException("Unexpected follow");
        public void RenderHints() => Calls.Add("RenderHints");
        public void RenderOverflow() => throw new InvalidOperationException("Unexpected overflow");
        public void RequestStop() => Calls.Add("RequestStop");
        public IDisposable? BeginActivitySourceReplacement() => throw new InvalidOperationException("Unexpected trace");

        public void Dispose()
        {
            EpicView.Dispose(); CommandLine.Dispose(); ActivityPane.Dispose(); Controller.Dispose();
        }
    }

    private sealed class RecordingDialogs : IOwnerConsoleDialogs
    {
        internal List<(string Title, string Text)> Messages { get; } = [];
        public Task ShowTextAsync(string title, string text) { Messages.Add((title, text)); return Task.CompletedTask; }
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected prompt");
    }
}
