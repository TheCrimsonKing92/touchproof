using System.Collections.Concurrent;
using System.Threading.Channels;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.Input;

// Parallel-safe: each test owns its controller, loaders, callback queues and cancellation.
public sealed class OwnerConsoleGoalDetailRefreshProgressTests(Xunit.ITestOutputHelper output)
{
    private const string Id = "22222222222222222222222222222222";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reload_RefreshBeforeEachCompletion_PublishesEveryAvailableResult()
    {
        using var fixture = await Fixture.CreateAsync();
        var session = fixture.Open(Id);
        var pending = await session.StartAsync();
        var samples = new List<string>();
        var statuses = new List<string?>();
        var pages = new List<string>();
        for (var index = 1; index <= 3; index++)
        {
            // Every burst must coalesce into one follow-up while this load is held.
            for (var refresh = 0; refresh < 3; refresh++)
            {
                fixture.Refresh();
                (await session.Ui.NextAsync())();
            }
            pending.Complete(Detail("result " + index));
            (await session.Ui.NextAsync())();
            samples.Add(session.Dialog.Current.Text);
            statuses.Add(session.Dialog.TitleStatus);
            pages.Add(string.Join(Environment.NewLine, session.Dialog.Page.Lines));
            output.WriteLine($"completed_load={index} visible={samples[^1]} status={statuses[^1]}");
            pending = await session.Loader.NextAsync();
            Assert.Equal(index + 1, session.Loader.Calls);
            Assert.Equal(1, session.Loader.Active);
        }
        pending.Complete(Detail("settled"));
        (await session.Ui.NextAsync())();
        await AwaitEvent(session.Dialog.LastLoad, "refresh cycle settled");

        // Assert after all observations so the baseline receipt records three invisible successes.
        Assert.Equal(["result 1", "result 2", "result 3"], samples);
        Assert.Equal(samples, pages);
        Assert.Equal(["refreshing", "refreshing", "refreshing"], statuses);
        Assert.Equal(["result 1", "result 2", "result 3", "settled"],
            session.Remembered.Select(content => content.Text));
        Assert.Equal("settled", session.Dialog.Current.Text);
        Assert.Null(session.Dialog.TitleStatus);
        Assert.Equal(4, session.Loader.Calls);
        Assert.Equal(1, session.Loader.MaximumActive);
        Assert.Equal(0, session.Loader.Active);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publication_QueuedRefresh_PreservesResultAndFollowUp(bool statusFirst)
    {
        using var fixture = await Fixture.CreateAsync();
        var session = fixture.Open(Id);
        var pending = await session.StartAsync();
        pending.Complete(Detail("first"));
        var publish = await session.Ui.NextAsync();
        fixture.Refresh();
        fixture.Refresh();
        var statuses = new[] { await session.Ui.NextAsync(), await session.Ui.NextAsync() };
        // A queued publication is a barrier: no next load can enter before it executes.
        Assert.Equal(1, session.Loader.Calls);
        if (statusFirst) foreach (var status in statuses) status();
        publish();
        var visible = session.Dialog.Current.Text;
        var titleStatus = session.Dialog.TitleStatus;
        var next = await session.Loader.NextAsync();
        if (!statusFirst) foreach (var status in statuses) status();
        next.Complete(Detail("second"));
        (await session.Ui.NextAsync())();
        await AwaitEvent(session.Dialog.LastLoad, "queued refresh settled");

        Assert.Equal("first", visible);
        Assert.Equal("refreshing", titleStatus);
        Assert.Equal("second", session.Dialog.Current.Text);
        Assert.Null(session.Dialog.TitleStatus);
        Assert.Equal(["first", "second"], session.Remembered.Select(content => content.Text));
        Assert.Equal(2, session.Loader.Calls);
        Assert.Equal(1, session.Loader.MaximumActive);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Lifetime_PendingWork_DropsLateMutations(bool cancel, bool queued)
    {
        using var fixture = await Fixture.CreateAsync();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var session = fixture.Open(Id, cancellation.Token, Detail("last good"));
        var changed = 0;
        session.Dialog.Changed += () => changed++;
        var pending = await session.StartAsync();
        fixture.Refresh();
        var status = await session.Ui.NextAsync();
        Action? publish = null;
        if (queued)
        {
            pending.Complete(Detail("late"));
            publish = await session.Ui.NextAsync();
        }
        var before = (session.Dialog.Current, session.Dialog.TitleStatus, changed);
        if (cancel) cancellation.Cancel(); else session.Dialog.Dispose();
        await AwaitEvent(session.Dialog.LastLoad, "cancelled refresh loop exited");
        status();
        publish?.Invoke();
        if (!queued) pending.Complete(Detail("late"));
        await AwaitEvent(pending.Exited.Task, "late loader exited");
        fixture.Refresh();
        session.Ui.Drain();

        Assert.Equal(before, (session.Dialog.Current, session.Dialog.TitleStatus, changed));
        Assert.Equal(["last good"], session.Dialog.Page.Lines);
        Assert.Empty(session.Remembered);
        Assert.Equal(1, session.Loader.Calls);
    }

    [Fact]
    public async Task Reload_NullAndError_RetainsContentThenRecovers()
    {
        using var fixture = await Fixture.CreateAsync();
        var session = fixture.Open(Id);
        var pending = await session.StartAsync();
        pending.Complete(Detail("last good"));
        (await session.Ui.NextAsync())();
        await AwaitEvent(session.Dialog.LastLoad, "initial success published");
        foreach (var fails in new[] { false, true })
        {
            fixture.Refresh();
            (await session.Ui.NextAsync())();
            pending = await session.Loader.NextAsync();
            if (fails) pending.Completion.SetException(new InvalidOperationException("offline"));
            else pending.Complete(null);
            (await session.Ui.NextAsync())();
            await AwaitEvent(session.Dialog.LastLoad, "failed refresh settled");
            Assert.Equal("last good", session.Dialog.Current.Text);
            Assert.Equal(["last good"], session.Dialog.Page.Lines);
            Assert.Equal("refresh failed: " + (fails ? "offline" : OwnerConsoleGoalDetailLoading.UnavailableText),
                session.Dialog.TitleStatus);
            Assert.Single(session.Remembered);
        }
        fixture.Refresh();
        (await session.Ui.NextAsync())();
        pending = await session.Loader.NextAsync();
        pending.Complete(Detail("recovered"));
        (await session.Ui.NextAsync())();
        await AwaitEvent(session.Dialog.LastLoad, "recovery settled");
        Assert.Equal("recovered", session.Dialog.Current.Text);
        Assert.Null(session.Dialog.TitleStatus);
        Assert.Equal(["last good", "recovered"], session.Remembered.Select(content => content.Text));
    }

    [Fact]
    public async Task Publication_RefreshPending_PreservesScrollAndActions()
    {
        using var fixture = await Fixture.CreateAsync();
        var question = new OwnerConsoleDecision("question", 1, Id, "22222222", OwnerQuestionKind.HumanInput,
            "Continue?", "Continue while refreshing?", null, null, null);
        var content = Detail(string.Join(Environment.NewLine, Enumerable.Range(0, 30).Select(i => "line " + i)))
            with { Question = question, Failure = "failure detail", EpicId = "epic", GateGoalId = Id };
        var session = fixture.Open(Id, initial: content);
        var pending = await session.StartAsync();
        session.Dialog.Page.Resize(60, 5);
        Assert.True(session.Dialog.Page.HandleKey(Key.PageDown));
        var offset = session.Dialog.Page.Offset;
        Assert.True(offset > 0);
        fixture.Refresh();
        (await session.Ui.NextAsync())();
        pending.Complete(content with { Text = content.Text.Replace("line", "fresh") });
        (await session.Ui.NextAsync())();
        var followUp = await session.Loader.NextAsync();

        Assert.StartsWith("fresh", session.Dialog.Current.Text);
        Assert.Equal("refreshing", session.Dialog.TitleStatus);
        Assert.Equal(offset, session.Dialog.Page.Offset);
        Assert.Equal(["Open question (d)", "Last failure (f)", "Epic (e)", "Gate view (g)"], session.Dialog.Actions);
        Assert.True(await session.Dialog.HandleKeyAsync('d'));
        Assert.Same(question, Assert.Single(session.Questions));
        Assert.True(await session.Dialog.HandleKeyAsync('f'));
        Assert.Equal(("Last failure", "failure detail"), Assert.Single(session.Texts));
        var closes = 0;
        session.Dialog.CloseRequested += () => closes++;
        Assert.True(await session.Dialog.HandleKeyAsync('q'));
        Assert.Equal(1, closes);
        session.Dialog.Dispose();
        followUp.Complete(Detail("late"));
        await AwaitEvent(session.Dialog.LastLoad, "closed action session exited");
        await AwaitEvent(followUp.Exited.Task, "closed action loader exited");
        Assert.Equal(offset, session.Dialog.Page.Offset);
    }

    private static OwnerConsoleGoalDialog.Content Detail(string text) => new(text, [], [], null, null, null);

    // Bounds diagnose a missing event only; no correctness verdict depends on elapsed time.
    private static async Task AwaitEvent(Task task, string name)
    {
        try { await task.WaitAsync(TimeSpan.FromSeconds(30), Token); }
        catch (TimeoutException ex) { throw new TimeoutException("Event did not occur: " + name, ex); }
    }

    private sealed class UiQueue
    {
        private readonly Channel<Action> _callbacks = Channel.CreateUnbounded<Action>();
        internal void Invoke(Action action) => Assert.True(_callbacks.Writer.TryWrite(action));
        internal async Task<Action> NextAsync()
        {
            var next = _callbacks.Reader.ReadAsync(Token).AsTask();
            await AwaitEvent(next, "UI callback queued");
            return await next;
        }
        internal void Drain() { while (_callbacks.Reader.TryRead(out var action)) action(); }
    }

    private sealed class PendingLoad
    {
        internal readonly TaskCompletionSource<OwnerConsoleGoalDialog.Content?> Completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Complete(OwnerConsoleGoalDialog.Content? content) => Completion.SetResult(content);
    }

    private sealed class Loader : IDisposable
    {
        private readonly Channel<PendingLoad> _requests = Channel.CreateUnbounded<PendingLoad>();
        private readonly ConcurrentBag<PendingLoad> _all = [];
        private int _calls, _active, _maximumActive;
        internal int Calls => Volatile.Read(ref _calls);
        internal int Active => Volatile.Read(ref _active);
        internal int MaximumActive => Volatile.Read(ref _maximumActive);
        internal async Task<OwnerConsoleGoalDialog.Content?> LoadAsync(CancellationToken token)
        {
            // Deliberately permit late completion even when the dialog cancels its wait.
            var request = new PendingLoad();
            _all.Add(request);
            Interlocked.Increment(ref _calls);
            var active = Interlocked.Increment(ref _active);
            for (var observed = MaximumActive; active > observed; observed = MaximumActive)
                if (Interlocked.CompareExchange(ref _maximumActive, active, observed) == observed) break;
            Assert.True(_requests.Writer.TryWrite(request));
            try { return await request.Completion.Task; }
            finally { Interlocked.Decrement(ref _active); request.Exited.TrySetResult(); }
        }
        internal async Task<PendingLoad> NextAsync()
        {
            var next = _requests.Reader.ReadAsync(Token).AsTask();
            await AwaitEvent(next, "loader entered");
            return await next;
        }
        public void Dispose() { foreach (var pending in _all) pending.Completion.TrySetResult(null); }
    }

    private sealed class Session : IDisposable
    {
        internal readonly Loader Loader = new();
        internal readonly UiQueue Ui = new();
        internal readonly List<OwnerConsoleGoalDialog.Content> Remembered = [];
        internal readonly List<OwnerConsoleDecision> Questions = [];
        internal readonly List<(string Title, string Text)> Texts = [];
        internal OwnerConsoleGoalDialog Dialog { get; }
        internal Session(OwnerConsoleScreenController controller, string id, CancellationToken token,
            OwnerConsoleGoalDialog.Content? initial)
        {
            Dialog = new(controller, initial ?? OwnerConsoleGoalDetailLoading.Placeholder(id, null), Loader.LoadAsync,
                question => { Questions.Add(question); return Task.CompletedTask; },
                (title, text) => { Texts.Add((title, text)); return Task.CompletedTask; }, token,
                loadOnStart: true, initialContentLoaded: initial is not null, remember: Remembered.Add);
        }
        internal async Task<PendingLoad> StartAsync()
        {
            Dialog.Start(Ui.Invoke);
            (await Ui.NextAsync())();
            return await Loader.NextAsync();
        }
        public void Dispose() { Dialog.Dispose(); Loader.Dispose(); Ui.Drain(); }
    }

    private sealed class Fixture(OwnerConsoleScreenController controller, OwnerConsoleViewModel model) : IDisposable
    {
        private readonly List<Session> _sessions = [];
        internal static async Task<Fixture> CreateAsync()
        {
            var harness = new OwnerConsoleHarness();
            harness.AddGoal(Id, "Selected goal", AgentRole.Developer);
            var controller = OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, new Dialogs(),
                harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, harness.Clock);
            var model = await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
                new Lookup(), harness.Clock).BuildAsync(new(harness.Clock.GetUtcNow(), null, [], 0));
            return new(controller, model);
        }
        internal Session Open(string id, CancellationToken? token = null, OwnerConsoleGoalDialog.Content? initial = null)
        {
            var session = new Session(controller, id, token ?? Token, initial);
            _sessions.Add(session);
            return session;
        }
        internal void Refresh() => controller.Apply(model);
        public void Dispose() { foreach (var session in _sessions) session.Dispose(); controller.Dispose(); }
    }

    private sealed class Lookup : IOwnerGoalEpicLookup
    { public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(""); }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("Unexpected confirmation");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected prompt");
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("Unexpected text dialog");
    }
}
