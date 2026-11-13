using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Fixture = OwnerConsoleEpicViewFixture;
using static OwnerConsoleEpicViewFixture;

// Parallel-safe: per-test kernels, in-memory sources and an uninitialized instance application.
public sealed class OwnerConsoleEpicViewPendingReadAndCancellationTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task BoardUpdatesCoalesceDuringPendingEpicRead()
    {
        using var fixture = new Fixture();
        fixture.Source.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = fixture.View.HandleKeyAsync(new Key('e'));
        // LoadStarted is the synchronization event; the test timeout only detects a hang.
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var oldRows = fixture.Source.Build(Now.AddHours(-24));
        fixture.Source.Epics[0] = fixture.Source.Epics[0] with { Title = "Updated alpha" };
        fixture.View.Render(fixture.Model);
        var pending = fixture.View.EpicView.LastLoad;
        fixture.View.Render(fixture.Model);
        Assert.Same(pending, fixture.View.EpicView.LastLoad);
        Assert.Equal(1, fixture.Source.Calls);
        var release = fixture.Source.Pending;
        fixture.Source.Pending = null;
        release.SetResult(oldRows);
        await opening.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, fixture.Source.Calls);
        Assert.Contains(fixture.View.EpicView.Lines, line => line.Contains("Updated alpha", StringComparison.Ordinal));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task ClosingCancelsPendingReadAndRejectsLateResults()
    {
        using var fixture = new Fixture();
        fixture.Source.Pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var opening = fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        await fixture.View.HandleKeyAsync(Key.Esc);
        await opening.WaitAsync(TestContext.Current.CancellationToken);
        var lines = fixture.View.EpicView.Lines.ToArray();
        fixture.Source.Pending.SetResult(fixture.Source.Build(null));
        fixture.View.Render(fixture.Model);
        AssertClosedAndFocused(fixture.View, OwnerConsolePane.Decisions);
        Assert.Equal(lines, fixture.View.EpicView.Lines);
        Assert.Equal(1, fixture.Source.Calls);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Open_HeldLoad_ShowsLoadingBeforeRowsAndLocalUpdateTime()
    {
        using var fixture = new Fixture();
        fixture.View.Window.Layout(new System.Drawing.Size(120, 30));
        var pending = fixture.Source.HoldNextLoad();
        var opening = fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var dialog = fixture.View.EpicView;

        Assert.True(dialog.Visible);
        Assert.Equal(["Loading epics (last 24 hours)…"], dialog.Lines);
        Assert.Contains("loading…", dialog.HeaderLine);
        Assert.DoesNotContain("Ctrl+R retry", dialog.HintText);
        fixture.Source.Release(pending, Now.AddHours(-24));
        await opening.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("> Alpha epic", dialog.Lines);
        AssertSummaries(fixture, Now.AddHours(-24));
        Assert.Contains("updated 01:00:00", dialog.HeaderLine);
        Assert.DoesNotContain("loading…", dialog.HeaderLine);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task WindowChange_HeldLoad_RetainsRowsWithPriorWindowAndNamesNewWindow()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Contains("> Alpha epic", fixture.View.EpicView.Lines);
        var pending = fixture.Source.HoldNextLoad();
        var changing = fixture.View.HandleKeyAsync(new Key('w'));
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("> Alpha epic", fixture.View.EpicView.Lines);
        Assert.Contains("refreshing…", fixture.View.EpicView.HeaderLine);
        Assert.Contains("last 7 days", fixture.View.EpicView.HeaderLine);
        Assert.Contains("last 24 hours", fixture.View.EpicView.HeaderLine);
        fixture.Source.Release(pending, Now.AddDays(-7));
        await changing.WaitAsync(TestContext.Current.CancellationToken);
        AssertSummaries(fixture, Now.AddDays(-7));
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Enter_HeldDetailLoad_RetainsListAndNamesSelectedEpic()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        Assert.Contains("> Beta epic", fixture.View.EpicView.Lines);
        var pending = fixture.Source.HoldNextLoad();
        var entering = fixture.View.HandleKeyAsync(Key.Enter);
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("> Beta epic", fixture.View.EpicView.Lines);
        Assert.Contains("refreshing…", fixture.View.EpicView.HeaderLine);
        Assert.Contains("requested Beta epic", fixture.View.EpicView.HeaderLine.Replace("\n", " "));
        Assert.Equal(1, fixture.Source.ListCalls);
        Assert.Equal(1, fixture.Source.DetailCalls);
        fixture.Source.Release(pending, Now.AddHours(-24));
        await entering.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal("Beta epic", fixture.View.EpicView.Lines[0]);
        Assert.Contains("Plan of record:", fixture.View.EpicView.Lines);
        Assert.Contains("updated 01:00:00", fixture.View.EpicView.HeaderLine);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Reopen_HeldLoad_RetainsPreviousContent()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Contains("> Alpha epic", fixture.View.EpicView.Lines);
        var prior = fixture.View.EpicView.Lines.ToArray();
        await fixture.View.HandleKeyAsync(Key.Esc);
        var pending = fixture.Source.HoldNextLoad();
        var opening = fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(prior, fixture.View.EpicView.Lines);
        Assert.Contains("(refreshing)", fixture.View.EpicView.Title.ToString());
        fixture.Source.Release(pending, Now.AddHours(-24));
        await opening.WaitAsync(TestContext.Current.CancellationToken);
        AssertSummaries(fixture, Now.AddHours(-24));
    }

    [Fact]
    public async Task Open_HostCancelledBeforeQueuedPublication_RejectsCompletedResult()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        var callbacks = new Queue<Action>();
        using var dialog = new OwnerConsoleEpicDialog(fixture.Controller, callbacks.Enqueue, cancellation.Token);
        await dialog.OpenAsync(OwnerConsolePane.Board);
        Assert.Single(callbacks);
        var prior = dialog.Lines.ToArray();
        cancellation.Cancel();
        callbacks.Dequeue()();
        Assert.Equal(prior, dialog.Lines);
        Assert.Equal(OwnerConsoleEpicLoadState.Loading, dialog.LoadState);
        Assert.DoesNotContain("updated", dialog.HeaderLine);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Navigation_PendingDetailThenListAndAnotherEpic_CancelsSupersededRequest()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        var pending = fixture.Source.HoldNextLoad();
        var entering = fixture.View.HandleKeyAsync(Key.Enter);
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var detailToken = fixture.Source.DetailToken;
        fixture.Source.Pending = null;
        await fixture.View.HandleKeyAsync(Key.Esc);
        await entering.WaitAsync(TestContext.Current.CancellationToken);
        Assert.True(detailToken.IsCancellationRequested);
        Assert.False(fixture.View.EpicView.ShowingDetail);
        Assert.Equal("beta", fixture.View.EpicView.SelectedEpicId);
        await fixture.View.HandleKeyAsync(Key.CursorUp);
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.Equal("alpha", fixture.View.EpicView.SelectedEpicId);
        Assert.Equal("Alpha epic", fixture.View.EpicView.Lines[0]);
        pending.SetResult(fixture.Source.Build(Now.AddHours(-24)));
        Assert.Equal("Alpha epic", fixture.View.EpicView.Lines[0]);
        Assert.Equal(2, fixture.Source.DetailCalls);
    }
}
