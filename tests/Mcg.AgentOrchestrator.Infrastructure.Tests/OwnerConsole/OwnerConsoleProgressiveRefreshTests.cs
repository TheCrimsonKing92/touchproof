using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;

// Parallel-safe: sources, event gates, clock and headless application are per test.
public sealed class OwnerConsoleProgressiveRefreshTests
{
    [Fact]
    public async Task ChangedDecisionIsUsableBeforeHeldBoardReadCompletes()
    {
        using var f = new Fixture();
        await Finished(f.Refresh.RefreshAsync());
        await Finished(f.View.PreviewLoad);
        var previousPreview = f.View.PreviewLoad;
        var previousBoard = f.Controller.Model!.Board;
        var entered = Signal();
        var release = Signal();
        var inputAt = f.Data.Clock.Now;
        DateTimeOffset? decisionAt = null;
        f.OnRendered = model =>
        {
            if (model.Decisions.Any(row => row.Id == "new-question")) decisionAt ??= f.Data.Clock.Now;
        };
        f.Data.Source.Items.Add(new("new-question", "11111111", OwnerQuestionKind.HumanInput, "Choose a branch?"));
        f.Data.State.Versions["11111111"] = "changed";
        f.Data.State.BeforeLoad = () =>
        {
            f.Data.Clock.Now = f.Data.Clock.Now.AddSeconds(3); // Model the unrelated loader's cost with virtual time.
            entered.TrySetResult();
            return release.Task;
        };
        var refresh = f.Refresh.RefreshAsync();
        try
        {
            await Finished(entered.Task);
            var decision = Assert.Single(f.Controller.Model!.Decisions);
            Assert.Equal("new-question", decision.Id);
            Assert.True(decision.CanAnswer);
            Assert.Equal(previousBoard.Select(row => row.Title), f.Controller.Model.Board.Select(row => row.Title));
            Assert.Same(previousPreview, f.View.PreviewLoad);
            Assert.Equal(inputAt, decisionAt); // Correct usable decision precedes the modeled three-second board read.
        }
        finally { release.TrySetResult(); await Finished(refresh); }
        Assert.NotSame(previousPreview, f.View.PreviewLoad);
        Assert.Empty(f.View.NoticeStrip.Visible);
    }

    [Fact]
    public async Task ChangedBoardAndGateRenderBeforeHeldActivityReadCompletes()
    {
        using var f = new Fixture();
        await Finished(f.Refresh.RefreshAsync());
        var oldActivity = f.Controller.Model!.Activity;
        var snapshot = f.Data.Harness.Kernel.ExportSnapshot();
        f.Data.Harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal.Id == "11111111"
                ? goal with { Objective = "Fresh title" } : goal).ToArray()
        });
        f.Data.State.Versions["11111111"] = "new";
        f.Gate = OwnerConsoleGateProgress.Idle() with { Kind = "fresh gate" };
        using var release = new ManualResetEventSlim();
        var entered = Signal();
        f.ReadLifecycle = () => { entered.TrySetResult(); release.Wait(); return []; };
        var refresh = f.Refresh.RefreshAsync();
        try
        {
            await Finished(entered.Task);
            Assert.Equal("Fresh title", f.Controller.Model!.Board.First(row => row.GoalId == "11111111").Title);
            Assert.Same(f.Gate, f.Controller.Model.Gate);
            Assert.Equal(oldActivity, f.Controller.Model.Activity);
        }
        finally { release.Set(); await Finished(refresh); }
    }

    [Fact]
    public async Task LaterBoardFailureKeepsPublishedDecisionAndRetriesBoard()
    {
        using var f = new Fixture();
        await Finished(f.Refresh.RefreshAsync());
        f.Data.Source.Items.Add(new("new-question", "11111111", OwnerQuestionKind.HumanInput, "Choose?"));
        f.Data.State.Versions["11111111"] = "failed-read";
        f.Data.State.FailLoad = true;
        await Finished(f.Refresh.RefreshAsync());
        Assert.Equal("new-question", Assert.Single(f.Controller.Model!.Decisions).Id);
        Assert.Contains(f.View.NoticeStrip.Visible, notice => notice.Text.Contains("snapshot read failed"));

        f.Data.State.FailLoad = false;
        f.Data.State.Visible.Remove("22222222");
        await Finished(f.Refresh.RefreshAsync());
        Assert.Equal("11111111", Assert.Single(f.Controller.Model!.Board).GoalId);
        Assert.Equal("new-question", Assert.Single(f.Controller.Model.Decisions).Id);
        Assert.Empty(f.View.NoticeStrip.Visible);
    }

    [Fact]
    public async Task SupersededPostedUpdatesCannotRestoreEarlierDecisions()
    {
        using var f = new Fixture();
        await Finished(f.Refresh.RefreshAsync());
        f.DeferPosts = true;
        f.Data.Source.Items.Add(new("old-question", "11111111", OwnerQuestionKind.HumanInput, "Old?"));
        await Finished(f.Refresh.RefreshAsync());
        f.Data.Source.Items.Clear();
        f.Data.Source.Items.Add(new("current-question", "11111111", OwnerQuestionKind.HumanInput, "Current?"));
        await Finished(f.Refresh.RefreshAsync());
        f.Rendered.Clear();
        f.FlushPosts();

        Assert.NotEmpty(f.Rendered);
        Assert.All(f.Rendered, model => Assert.Equal("current-question", Assert.Single(model.Decisions).Id));
        Assert.Equal("current-question", Assert.Single(f.Controller.Model!.Decisions).Id);
    }

    [Fact]
    public async Task CanceledRefreshCannotPublishQueuedPartialOrLateBoard()
    {
        using var stop = new CancellationTokenSource();
        using var f = new Fixture(stop.Token);
        await Finished(f.Refresh.RefreshAsync());
        f.DeferPosts = true;
        f.Data.Source.Items.Add(new("canceled-question", "11111111", OwnerQuestionKind.HumanInput, "Choose?"));
        f.Data.State.Versions["11111111"] = "changed";
        var entered = Signal();
        var release = Signal();
        f.Data.State.BeforeLoad = () => { entered.TrySetResult(); return release.Task; };
        var refresh = f.Refresh.RefreshAsync();
        try
        {
            await Finished(entered.Task);
            stop.Cancel();
            await Finished(refresh);
            f.FlushPosts();
            Assert.Empty(f.Controller.Model!.Decisions);
        }
        finally
        {
            release.TrySetResult();
            try { await Finished(f.Refresh.Completion); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        f.FlushPosts();
        Assert.Empty(f.Controller.Model!.Decisions);
    }

    [Fact]
    public async Task PeriodicRefreshKeepsOnePaintAndIndependentDecisionMarkerRemainsReusable()
    {
        using var f = new Fixture();
        await Finished(f.Refresh.RefreshAsync());
        f.Data.Source.Items.Add(new("question", "11111111", OwnerQuestionKind.HumanInput, "Choose?"));
        f.Data.Marker = "changed";
        await Finished(f.Refresh.RefreshAsync(scope: OwnerConsoleRefreshScope.All));
        Assert.Equal(2, f.Data.Source.Reads);
        f.Rendered.Clear();
        f.Data.Clock.Now = f.Data.Clock.Now.AddSeconds(30);
        await Finished(f.Refresh.RefreshAsync(skipIfBusy: true));

        Assert.Single(f.Rendered);
        Assert.Equal(2, f.Data.Source.Reads);
        Assert.Equal("question", Assert.Single(f.Controller.Model!.Decisions).Id);
    }

    [Fact]
    public async Task MutationDuringHeldBoardReadRemainsPendingAndPublishesNextDecision()
    {
        using var f = new Fixture();
        await Finished(f.Refresh.RefreshAsync());
        f.Data.Source.Items.Add(new("first", "11111111", OwnerQuestionKind.HumanInput, "First?"));
        f.Data.Marker = "first";
        f.Data.State.Versions["11111111"] = "changed";
        var entered = Signal();
        var release = Signal();
        f.Data.State.BeforeLoad = () => { entered.TrySetResult(); return release.Task; };
        var coalescer = new OwnerConsoleRefreshCoalescer((items, scope) => f.Refresh.RefreshAsync(items, scope: scope));
        coalescer.Request(null, OwnerConsoleRefreshScope.All);
        try
        {
            await Finished(entered.Task);
            Assert.Equal("first", Assert.Single(f.Controller.Model!.Decisions).Id);
            f.Data.Source.Items.Clear();
            f.Data.Source.Items.Add(new("second", "11111111", OwnerQuestionKind.HumanInput, "Second?"));
            f.Data.Marker = "second";
            coalescer.Request(null, OwnerConsoleRefreshScope.All);
        }
        finally { release.TrySetResult(); await Finished(coalescer.Completion); }

        Assert.Equal("second", Assert.Single(f.Controller.Model!.Decisions).Id);
        Assert.Equal(3, f.Data.Source.Reads);
    }

    [Fact]
    public async Task RepeatedProgressiveReplacementAndRemovalRetainsOnlyLiveDecisionNumbers()
    {
        using var f = new Fixture();
        await Finished(f.Refresh.RefreshAsync());
        for (var cycle = 0; cycle < 40; cycle++)
        {
            f.Data.Clock.Now = f.Data.Clock.Now.AddSeconds(30);
            f.Data.Source.Items.Clear();
            var id = $"question-{cycle}";
            f.Data.Source.Items.Add(new(id, "11111111", OwnerQuestionKind.HumanInput, $"Choose {cycle}?"));
            f.Data.Marker = id;
            f.Data.State.Versions["11111111"] = id;
            var entered = Signal();
            var release = Signal();
            f.Data.State.BeforeLoad = () => { entered.TrySetResult(); return release.Task; };
            var refresh = f.Refresh.RefreshAsync(scope: OwnerConsoleRefreshScope.All);
            try
            {
                await Finished(entered.Task);
                Assert.Equal(id, Assert.Single(f.Controller.Model!.Decisions).Id);
                Assert.Equal(1, f.Builder.Retained.Numbers);
            }
            finally { release.TrySetResult(); await Finished(refresh); }
            f.Data.State.BeforeLoad = null;
            f.Data.Source.Items.Clear();
            f.Data.Marker = $"removed-{cycle}";
            await Finished(f.Refresh.RefreshAsync(scope: OwnerConsoleRefreshScope.All));
            Assert.Empty(f.Controller.Model.Decisions);
            Assert.Equal(0, f.Builder.Retained.Numbers);
            f.Rendered.Clear();
        }
    }

    [Fact]
    public async Task FirstRefreshPreservesComposedStartupScopeWhilePublishingDecision()
    {
        using var f = new Fixture();
        var startup = await f.Data.Builder.BuildBoardAsync();
        var scope = new OwnerConsoleBoardScope("remote", 1, "home", ["remote"]);
        startup = startup with
        {
            BoardScope = scope,
            Board = [new("foreign", "", "Foreign title", "Active", "working", null, "foreign-id")
                { Project = "remote", IsForeign = true }]
        };
        f.View.Render(startup);
        f.Refresh.SeedPublished(startup);
        f.Data.Source.Items.Add(new("question", "11111111", OwnerQuestionKind.HumanInput, "Choose?"));
        var entered = Signal();
        var release = Signal();
        f.Data.State.BeforeLoad = () => { entered.TrySetResult(); return release.Task; };
        var refresh = f.Refresh.RefreshAsync();
        try
        {
            await Finished(entered.Task);
            Assert.Same(scope, f.Controller.Model!.BoardScope);
            Assert.Equal("remote", Assert.Single(f.Controller.Model.Board).Project);
            Assert.Equal("question", Assert.Single(f.Controller.Model.Decisions).Id);
        }
        finally { release.TrySetResult(); await Finished(refresh); }
    }

    [Fact]
    public async Task LateStartupSeedCannotReplaceNewerDecisionsOnPaneOnlyRefresh()
    {
        using var f = new Fixture();
        var stale = await f.Data.Builder.BuildBoardAsync();
        f.Data.Source.Items.Add(new("current", "11111111", OwnerQuestionKind.HumanInput, "Current?"));
        await Finished(f.Refresh.RefreshAsync());
        f.Refresh.SeedPublished(stale);
        await Finished(f.Refresh.RefreshAsync([new(f.Data.Clock.Now, "gate-progress", "11111111", "")],
            scope: OwnerConsoleRefreshScope.None));

        Assert.Equal("current", Assert.Single(f.Controller.Model!.Decisions).Id);
    }

    [Fact]
    public async Task LoadingStartupSeedCannotMakeFirstEventSkipDecisionRead()
    {
        using var f = new Fixture();
        var loading = OwnerConsoleStartupLoader.LoadingBoard(await f.Data.Builder.BuildBoardAsync());
        f.Refresh.SeedPublished(loading);
        f.Data.Source.Items.Add(new("current", "11111111", OwnerQuestionKind.HumanInput, "Current?"));
        await Finished(f.Refresh.RefreshAsync([new(f.Data.Clock.Now, "gate-progress", "11111111", "")],
            scope: OwnerConsoleRefreshScope.None));

        Assert.Equal("current", Assert.Single(f.Controller.Model!.Decisions).Id);
        Assert.Null(f.Controller.Model.DecisionsState);
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static Task Finished(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private sealed class Fixture : IDisposable
    {
        internal readonly OwnerConsoleRefreshReuseFixture Data = new();
        internal readonly OwnerConsoleScreenController Controller;
        internal readonly OwnerConsoleFullScreenView View;
        internal readonly OwnerRefreshCoordinator Refresh;
        internal readonly OwnerConsoleViewModelBuilder Builder;
        internal readonly ConcurrentQueue<OwnerConsoleViewModel> Rendered = new();
        internal Action<OwnerConsoleViewModel>? OnRendered;
        internal Func<IReadOnlyList<OwnerConductEvent>> ReadLifecycle = () => [];
        internal OwnerConsoleGateProgress? Gate;
        internal bool DeferPosts;
        private readonly ConcurrentQueue<Action> _posts = new();
        private readonly IApplication _app = Application.Create();

        internal Fixture(CancellationToken token = default)
        {
            var h = Data.Harness;
            Builder = new OwnerConsoleViewModelBuilder(Data.State, Data.Source, h.Liveness, new Epics(), Data.Clock,
                snapshots: Data.Snapshots, decisionsMarker: () => Data.Marker, gateProgress: _ => Gate);
            Controller = OwnerConsoleControllerFactory.Create(Data.Source, h.Answers, new Dialogs(),
                h.State, h.Tail, h.Conductor, h.DigestReport, h.Digest, Data.Clock);
            View = new(_app, Controller, () => Task.CompletedTask, clock: Data.Clock);
            Refresh = new(Data.Clock, token, Builder, null, Data.Snapshots, Data.Timings, Data.Filter, null,
                (_, _) => ReadLifecycle(), () => 0, _ => { }, action => action(), (_, action) =>
                {
                    void Render()
                    {
                        var previous = Controller.Model;
                        action();
                        if (!ReferenceEquals(previous, Controller.Model))
                        {
                            Rendered.Enqueue(Controller.Model!);
                            OnRendered?.Invoke(Controller.Model!);
                        }
                    }
                    if (DeferPosts) _posts.Enqueue(Render); else Render();
                }, () => View);
        }

        internal void FlushPosts() { while (_posts.TryDequeue(out var action)) action(); }
        public void Dispose() { Refresh.Dispose(); View.Dispose(); _app.Dispose(); Data.Dispose(); }
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
    }
}
