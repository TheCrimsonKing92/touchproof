using System.Collections.Concurrent;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Terminal.Gui.App;

// Parallel-safe: all readers, event gates, dispatch queues and headless views are per test.
public sealed class OwnerActivityEvidenceLoaderTests
{
    [Fact]
    public async Task StartupFill_PostedActionsOnlyApplyResolvedEvidence()
    {
        var failure = "resolved-before-post";
        using var fixture = new Fixture(_ => new([], [], FirstFailure: failure));

        await Finished(fixture.FillAsync(Failed("startup")), "Startup fill");

        Assert.Equal(1, fixture.EvidenceReads);
        Assert.Equal(2, fixture.StartupPosts.Count);
        Assert.Empty(fixture.StartupModels);
        failure = "changed-after-post";
        fixture.StartupPosts.RunAll();

        Assert.Equal(1, fixture.EvidenceReads);
        Assert.Equal(0, fixture.ReadsDuringPostedAction);
        Assert.Equal(2, fixture.StartupModels.Count);
        Assert.Contains(fixture.View.ActivityLines, line => line.Contains("resolved-before-post"));
        Assert.DoesNotContain(fixture.View.ActivityLines, line => line.Contains("changed-after-post"));
    }

    [Fact]
    public async Task StaleStartupRead_AfterNewerRefreshPublished_IsDiscarded()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(item =>
        {
            if (OwnerEventFields.Field(item, "reason") == "stale")
            {
                entered.TrySetResult();
                Finished(release.Task, "Slow evidence release").GetAwaiter().GetResult();
            }
            return Evidence(item);
        });
        var fill = fixture.FillAsync(Failed("stale"));
        try
        {
            await Finished(entered.Task, "Slow evidence entry");
            await Finished(fixture.Refresh.RefreshAsync([Failed("fresh")]), "Newer refresh");
            Assert.Equal(1, fixture.RefreshPosts.Count);
            fixture.RefreshPosts.RunAll();
            Assert.Contains(fixture.View.ActivityLines, line => line.Contains("fresh"));
        }
        finally
        {
            release.TrySetResult();
            await Finished(fill, "Released startup fill");
        }
        fixture.StartupPosts.RunAll();

        Assert.Equal(2, fixture.EvidenceReads);
        Assert.Empty(fixture.StartupModels);
        Assert.Contains(fixture.View.ActivityLines, line => line.Contains("fresh"));
        Assert.DoesNotContain(fixture.View.ActivityLines, line => line.Contains("stale"));
    }

    [Fact]
    public async Task QueuedStartupAction_AfterNewerRefreshBegan_DoesNotApply()
    {
        using var fixture = new Fixture(Evidence);
        await Finished(fixture.FillAsync(Failed("stale")), "Startup fill");
        Assert.Equal(1, fixture.EvidenceReads);
        Assert.Equal(2, fixture.StartupPosts.Count);
        Assert.Empty(fixture.StartupModels);

        await Finished(fixture.Refresh.RefreshAsync([Failed("fresh")]), "Newer refresh");
        Assert.Equal(1, fixture.RefreshPosts.Count);
        fixture.RefreshPosts.RunAll();
        var freshLines = fixture.View.ActivityLines.ToArray();
        Assert.Contains(freshLines, line => line.Contains("fresh"));
        fixture.StartupPosts.RunAll();

        Assert.Empty(fixture.StartupModels);
        Assert.Equal(freshLines, fixture.View.ActivityLines);
        Assert.DoesNotContain(fixture.View.ActivityLines, line => line.Contains("stale"));
    }

    [Fact]
    public async Task Refresh_ResolvesEvidenceBeforePost()
    {
        var failure = "resolved-before-post";
        using var fixture = new Fixture(_ => new([], [], FirstFailure: failure));
        var readsAtPost = 0;
        fixture.RefreshPosts.BeforePost = () => readsAtPost = fixture.EvidenceReads;

        await Finished(fixture.Refresh.RefreshAsync([Failed("refresh")]), "Refresh");

        Assert.Equal(1, readsAtPost);
        Assert.Equal(1, fixture.RefreshPosts.Count);
        failure = "changed-after-post";
        fixture.RefreshPosts.RunAll();

        Assert.Equal(1, fixture.EvidenceReads);
        Assert.Equal(0, fixture.ReadsDuringPostedAction);
        Assert.Contains(fixture.View.ActivityLines, line => line.Contains("resolved-before-post"));
        Assert.DoesNotContain(fixture.View.ActivityLines, line => line.Contains("changed-after-post"));
    }

    [Fact]
    public async Task QueuedRefreshAction_AfterNewerRefreshBegan_DoesNotApply()
    {
        using var fixture = new Fixture(Evidence);
        await Finished(fixture.Refresh.RefreshAsync([Failed("stale")]), "Older refresh");
        Assert.Equal(1, fixture.RefreshPosts.Count);
        var older = fixture.RefreshPosts.Take();
        await Finished(fixture.Refresh.RefreshAsync([Failed("fresh")]), "Newer refresh");
        Assert.Equal(1, fixture.RefreshPosts.Count);
        fixture.RefreshPosts.RunAll();
        var freshLines = fixture.View.ActivityLines.ToArray();
        Assert.Contains(freshLines, line => line.Contains("fresh"));

        fixture.RefreshPosts.Run(older);

        Assert.Equal(freshLines, fixture.View.ActivityLines);
    }

    private static OwnerConductEvent Failed(string reason) =>
        new(DateTimeOffset.UnixEpoch, "acceptance", "11111111", $"result=failed reason={reason}");

    private static OwnerActivityTestEvidence Evidence(OwnerConductEvent item) =>
        new([], [], FirstFailure: OwnerEventFields.Field(item, "reason"));

    private static async Task Finished(Task operation, string signal)
    {
        try { await operation.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken); }
        catch (TimeoutException) { Assert.Fail($"{signal} was not signalled."); }
    }

    private sealed class RecordingDispatcher
    {
        private readonly ConcurrentQueue<Action> _actions = new();
        private int _executing;
        internal bool Executing => Volatile.Read(ref _executing) != 0;
        internal int Count => _actions.Count;
        internal Action? BeforePost;

        internal void Post(Action action) { BeforePost?.Invoke(); _actions.Enqueue(action); }

        internal Action Take()
        {
            Assert.True(_actions.TryDequeue(out var action), "A posted action must be present.");
            return action!;
        }

        internal void Run(Action action)
        {
            Volatile.Write(ref _executing, 1);
            try { action(); }
            finally { Volatile.Write(ref _executing, 0); }
        }

        internal void RunAll()
        {
            while (_actions.TryDequeue(out var action)) Run(action);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly OwnerConsoleHarness _harness = new();
        private readonly IApplication _app = Application.Create();
        internal readonly OwnerConsoleViewModelBuilder Builder;
        internal readonly OwnerConsoleFullScreenView View;
        internal readonly OwnerRefreshCoordinator Refresh;
        internal readonly RecordingDispatcher StartupPosts = new();
        internal readonly RecordingDispatcher RefreshPosts = new();
        internal readonly List<OwnerConsoleViewModel> StartupModels = [];
        private readonly OwnerActivityEvidenceLoader _evidence;
        internal int EvidenceReads;
        internal int ReadsDuringPostedAction;

        internal Fixture(Func<OwnerConductEvent, OwnerActivityTestEvidence?> read)
        {
            var snapshots = new OwnerConsoleRefreshGoalSnapshots(_harness.State);
            Builder = new(_harness.State, _harness.Questions, _harness.Liveness, new Epics(), _harness.Clock,
                snapshots: snapshots);
            _evidence = new(item =>
            {
                Interlocked.Increment(ref EvidenceReads);
                if (StartupPosts.Executing || RefreshPosts.Executing) Interlocked.Increment(ref ReadsDuringPostedAction);
                return read(item);
            });
            var controller = OwnerConsoleControllerFactory.Create(_harness.Questions, _harness.Answers, new Dialogs(),
                _harness.State, _harness.Tail, _harness.Conductor, _harness.DigestReport, _harness.Digest, _harness.Clock);
            View = new(_app, controller, () => Task.CompletedTask, clock: _harness.Clock);
            Refresh = new(_harness.Clock, TestContext.Current.CancellationToken, Builder, null, snapshots,
                new(_harness.Clock), new(), null, (_, _) => [], () => 0, _ => { }, action => action(),
                (_, action) => RefreshPosts.Post(action), () => View, _evidence);
        }

        internal async Task FillAsync(OwnerConductEvent item)
        {
            var board = OwnerConsoleStartupLoader.LoadingBoard(await Builder.BuildBoardAsync());
            View.Render(board);
            var startup = new OwnerConsoleStartupLoader(Builder, new Activity(item), StartupPosts.Post,
                evidence: _evidence);
            await startup.FillAsync(board, model => { StartupModels.Add(model); View.Render(model); },
                Refresh.StartupInputs(), _ => { }, TestContext.Current.CancellationToken);
        }

        public void Dispose() { Refresh.Dispose(); View.Dispose(); _app.Dispose(); }
    }

    private sealed class Activity(OwnerConductEvent item) : IOwnerConsoleActivityLoader
    {
        public Task<OwnerConsoleActivityLoad> LoadAsync(IReadOnlyCollection<string> ids, CancellationToken token) =>
            Task.FromResult(new OwnerConsoleActivityLoad([item], item.Timestamp));
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected dialog.");
    }
}
