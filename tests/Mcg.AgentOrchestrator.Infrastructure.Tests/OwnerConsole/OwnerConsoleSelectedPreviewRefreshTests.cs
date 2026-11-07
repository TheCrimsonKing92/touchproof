using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;

// Parallel-safe: kernels, clocks, load gates and uninitialized GUI views are instance-owned.
public sealed class OwnerConsoleSelectedPreviewRefreshTests
{
    private const string Id = "11111111-selected";
    private const string Title = "Improve the owner console";
    private const string Stage = "waiting for a free worker";
    private const string NextStage = "question for you";

    [Fact]
    public async Task Refresh_LoaderPublishesDuringRender_KeepsRenderedStage()
    {
        var entered = Signal();
        var release = Signal();
        var loads = 0;
        var summary = new OwnerGoalChangeSummaryQuery(promotion: async (_, token) =>
        {
            if (Interlocked.Increment(ref loads) > 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(token);
            }
            return null;
        });
        using var controller = CreateController(summary);
        using IApplication app = Terminal.Gui.App.Application.Create();
        Assert.False(app.Initialized);
        var clock = new PublicationClock();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask, clock: clock);
        var model = Model();
        view.Render(model);
        await TestHangGuard.WaitAsync(view.PreviewLoad, "baseline selected preview publication");
        view.Window.Layout(new Size(100, 40));
        Assert.Equal(Expected(Stage), view.PreviewText);

        var previousLoad = view.PreviewLoad;
        var renderThread = Environment.CurrentManagedThreadId;
        var hookCount = 0;
        clock.BeforeRead = () =>
        {
            if (Environment.CurrentManagedThreadId != renderThread ||
                ReferenceEquals(view.PreviewLoad, previousLoad) || hookCount != 0) return;
            hookCount++;
            Wait(entered.Task, "refresh loader entered its publication gate");
            Assert.False(view.PreviewLoad.IsCompleted);
            // Before the fix, the redundant post-Select render captured the stale last-good row
            // before this clock read, then overwrote the loader's inline publication after LastLoad
            // completed. src/Mcg.AgentOrchestrator.App/Console/OwnerConsoleFullScreenView.cs:556
            // now skips that render; this same hook runs in RenderNotices instead. Keep the loader
            // gated until here so even a fast loader cannot publish before the stale row capture.
            release.TrySetResult();
            Wait(view.PreviewLoad, "refresh publication completed inside Render");
        };
        try
        {
            view.Render(model with { Board = [model.Board[0] with { Stage = NextStage }] });
            Assert.Equal(1, hookCount);
            await TestHangGuard.WaitAsync(view.PreviewLoad, "refreshed selected preview publication");
            Assert.Equal(Expected(NextStage), view.PreviewText);
        }
        finally
        {
            clock.BeforeRead = null;
            release.TrySetResult();
        }
    }

    [Fact]
    public async Task Refresh_WhileFirstLoadInFlight_CompletesOnlyWithRenderedStage()
    {
        var entered = Signal();
        var release = Signal();
        var summary = new OwnerGoalChangeSummaryQuery(promotion: async (_, token) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(token);
            return null;
        });
        using var controller = CreateController(summary);
        using IApplication app = Terminal.Gui.App.Application.Create();
        Assert.False(app.Initialized);
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask,
            clock: new OwnerConsoleTestClock());
        var model = Model();
        Task completion;
        try
        {
            view.Render(model);
            await TestHangGuard.WaitAsync(entered.Task, "first selected summary load entered");
            Assert.False(view.PreviewLoad.IsCompleted);
            view.Window.Layout(new Size(100, 40));
            view.Render(model with { Board = [model.Board[0] with { Stage = NextStage }] });
            completion = view.PreviewLoad;
            Assert.False(completion.IsCompleted);
        }
        finally { release.TrySetResult(); }
        await TestHangGuard.WaitAsync(completion, "overlapping refresh published the rendered stage");
        Assert.Equal(Expected(NextStage), view.PreviewText);
    }

    [Fact]
    public async Task Refresh_AlternatingStages_EachAwaitSeesRenderedStage()
    {
        using var controller = CreateController(new OwnerGoalChangeSummaryQuery());
        using IApplication app = Terminal.Gui.App.Application.Create();
        Assert.False(app.Initialized);
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask,
            clock: new OwnerConsoleTestClock());
        var model = Model();
        view.Render(model);
        await TestHangGuard.WaitAsync(view.PreviewLoad, "baseline selected preview publication");
        view.Window.Layout(new Size(100, 40));
        Assert.Equal(Expected(Stage), view.PreviewText);

        for (var i = 0; i < 200; i++)
        {
            var stage = i % 2 == 0 ? NextStage : Stage;
            view.Render(model with { Board = [model.Board[0] with { Stage = stage }] });
            await TestHangGuard.WaitAsync(view.PreviewLoad, $"alternating stage publication {i}");
            Assert.Equal(Expected(stage), view.PreviewText);
        }
    }

    private static OwnerConsoleScreenController CreateController(OwnerGoalChangeSummaryQuery summary)
    {
        var clock = new OwnerConsoleTestClock();
        var harness = new OwnerConsoleHarness(clock);
        harness.AddGoal(Id, "# " + Title + "\nMore detail", AgentRole.Developer);
        return OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers,
            new Dialogs(), harness.State, harness.Tail, harness.Conductor, harness.DigestReport, harness.Digest, clock,
            goalDetail: new OwnerGoalDetailQuery(harness.State, harness.Tail, summary: summary));
    }

    private static OwnerConsoleViewModel Model() => new(new(false, 1, 0, 0, null, 0), [],
        [new("11111111", "Console", Title, "Active", Stage, null, Id)], []);

    private static string Expected(string stage) => string.Join('\n', Title, "No committed changes yet", stage);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static void Wait(Task task, string eventName)
    {
        if (!task.Wait(TestHangGuard.Bound, TestContext.Current.CancellationToken))
            throw new TimeoutException($"Hang guard: {eventName} did not complete.");
    }

    private sealed class PublicationClock : TimeProvider
    {
        internal Action? BeforeRead { get; set; }
        public override DateTimeOffset GetUtcNow()
        {
            BeforeRead?.Invoke();
            return DateTimeOffset.UnixEpoch;
        }
        public override TimeZoneInfo LocalTimeZone
        {
            get
            {
                BeforeRead?.Invoke();
                return TimeZoneInfo.Utc;
            }
        }
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task ShowTextAsync(string title, string text) => throw new InvalidOperationException("No dialogs expected");
        public Task<bool> ConfirmAsync(string title, string text) => throw new InvalidOperationException("No writes");
        public Task<string?> PromptTextAsync(string title, string text) => throw new InvalidOperationException("No writes");
    }
}
