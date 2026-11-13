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
public sealed class OwnerConsoleEpicViewRefreshOrderingTests
{
    [Fact]
    public async Task OpenViewRefreshesCountsAndDetailWithBoardUpdates()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        await fixture.View.HandleKeyAsync(Key.Enter);
        fixture.Source.Epics[1] = fixture.Source.Epics[1] with { Description = "New reviewed plan" };
        fixture.Source.Plans["beta"] = new(new("beta", "Updated epic bar", [], []), [], []);
        fixture.Source.Goals.Add(new("b-newest", "Active", "New live goal", Now.ToString("O")));
        fixture.Source.Members.Add(new("beta", PortfolioMemberKind.Goal, "b-newest", Now, "test"));
        fixture.Harness.AddGoal("b-newest", "New live goal", AgentRole.Reviewer);
        fixture.View.Render(fixture.Model);
        await fixture.View.EpicView.LastLoad;
        Assert.Equal("beta", fixture.View.EpicView.SelectedEpicId);
        Assert.Contains("New reviewed plan", fixture.View.EpicView.Lines);
        Assert.Contains("Bar: Updated epic bar", fixture.View.EpicView.Lines);
        Assert.Equal(["beta", "beta"], fixture.Source.PlanIds);
        Assert.Contains("b-newest  New live goal  Reviewer", fixture.View.EpicView.Lines);

        await fixture.View.HandleKeyAsync(Key.Esc);
        AssertSummaries(fixture, Now.AddHours(-24));
        await fixture.View.HandleKeyAsync(Key.Esc);
        var calls = fixture.Source.Calls;
        fixture.View.Render(fixture.Model);
        Assert.Equal(calls, fixture.Source.Calls);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelApplied_HeldRefresh_KeepsContentWithRefreshingHeader(bool detail)
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        if (detail) await fixture.View.HandleKeyAsync(Key.Enter);
        var dialog = fixture.View.EpicView;
        var currentLines = dialog.Lines.ToArray();
        var pending = fixture.Source.HoldNextLoad();
        fixture.View.Render(fixture.Model);
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        var refreshing = dialog.LastLoad;
        Assert.Equal(currentLines, dialog.Lines);
        Assert.Contains("refreshing…", dialog.HeaderLine);
        Assert.DoesNotContain("Ctrl+R retry", dialog.HintText);
        fixture.Source.Release(pending, Now.AddHours(-24));
        await refreshing.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Contains("updated 01:00:00", dialog.HeaderLine);
        Assert.DoesNotContain("refreshing…", dialog.HeaderLine);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ModelApplied_SustainedUpdates_PublishesEachReadWhileRefreshing(bool detail)
    {
        using var fixture = new Fixture();
        var callbacks = System.Threading.Channels.Channel.CreateUnbounded<Action>();
        using var dialog = new OwnerConsoleEpicDialog(fixture.Controller,
            action => Assert.True(callbacks.Writer.TryWrite(action)), CancellationToken.None);
        var pending = fixture.Source.HoldNextLoad();
        var opening = dialog.OpenAsync(OwnerConsolePane.Board, detail ? "alpha" : null);
        var token = TestContext.Current.CancellationToken;
        await fixture.Source.LoadStarted.Task.WaitAsync(token);
        Assert.Equal(OwnerConsoleEpicLoadState.Loading, dialog.LoadState);

        for (var read = 1; read <= 4; read++)
        {
            var calls = fixture.Source.Calls;
            // Drive the production ModelApplied route while this read is held.
            for (var update = 0; update < 3; update++) fixture.Controller.Apply(fixture.Model);
            Assert.Equal(calls, fixture.Source.Calls);
            fixture.Time.Current = Now.AddMinutes(read);
            var title = $"Alpha read {read}";
            fixture.Source.Epics[0] = fixture.Source.Epics[0] with { Title = title };
            var next = fixture.Source.HoldNextLoad();
            pending.SetResult(fixture.Source.Build(Now.AddHours(-24)));
            await fixture.Source.LoadStarted.Task.WaitAsync(token);
            // Consume the completed publication with the follow-up already in flight.
            (await callbacks.Reader.ReadAsync(token))();
            Assert.Equal(calls + 1, fixture.Source.Calls);
            Assert.Equal(OwnerConsoleEpicLoadState.Loaded, dialog.LoadState);
            Assert.Contains(detail ? title : $"> {title}", dialog.Lines);
            Assert.Equal("alpha", dialog.SelectedEpicId);
            Assert.Equal(detail, dialog.ShowingDetail);
            var header = dialog.HeaderLine.Replace("\n", " ");
            Assert.Contains("refreshing…", header);
            Assert.Contains($"updated 01:0{read}:00", header);
            Assert.False(opening.IsCompleted);
            pending = next;
        }

        fixture.Time.Current = Now.AddMinutes(5);
        fixture.Source.Release(pending, Now.AddHours(-24));
        await opening.WaitAsync(token);
        (await callbacks.Reader.ReadAsync(token))();
        Assert.Equal(OwnerConsoleEpicLoadState.Loaded, dialog.LoadState);
        Assert.Contains("updated 01:05:00", dialog.HeaderLine);
        Assert.DoesNotContain("refreshing", dialog.HeaderLine);
        Assert.False(callbacks.Reader.TryRead(out _));
        Assert.Equal(5, fixture.Source.Calls);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_SameCycleReversedCallbacks_PreservesNewestPublication(bool olderFailure)
    {
        using var fixture = new Fixture();
        var callbacks = System.Threading.Channels.Channel.CreateUnbounded<Action>();
        using var dialog = new OwnerConsoleEpicDialog(fixture.Controller,
            action => Assert.True(callbacks.Writer.TryWrite(action)), CancellationToken.None);
        var token = TestContext.Current.CancellationToken;
        await dialog.OpenAsync(OwnerConsolePane.Board);
        (await callbacks.Reader.ReadAsync(token))();

        var pending = fixture.Source.HoldNextLoad();
        var refreshing = dialog.ReloadAsync();
        await fixture.Source.LoadStarted.Task.WaitAsync(token);
        fixture.Controller.Apply(fixture.Model);
        fixture.Time.Current = Now.AddMinutes(1);
        fixture.Source.Epics[0] = fixture.Source.Epics[0] with { Title = "Obsolete alpha" };
        var next = fixture.Source.HoldNextLoad();
        if (olderFailure) pending.SetException(new InvalidOperationException("obsolete error"));
        else pending.SetResult(fixture.Source.Build(Now.AddHours(-24)));
        await fixture.Source.LoadStarted.Task.WaitAsync(token);
        var oldPublication = await callbacks.Reader.ReadAsync(token);

        fixture.Time.Current = Now.AddMinutes(2);
        fixture.Source.Epics[0] = fixture.Source.Epics[0] with { Title = "Current alpha" };
        fixture.Source.Release(next, Now.AddHours(-24));
        await refreshing.WaitAsync(token);
        (await callbacks.Reader.ReadAsync(token))();
        Assert.Contains("> Current alpha", dialog.Lines);
        var currentLines = dialog.Lines.ToArray();
        var currentHeader = dialog.HeaderLine;
        oldPublication();
        Assert.Equal(currentLines, dialog.Lines);
        Assert.Equal(currentHeader, dialog.HeaderLine);
        Assert.Equal(OwnerConsoleEpicLoadState.Loaded, dialog.LoadState);
        Assert.Contains("updated 01:02:00", dialog.HeaderLine);
        Assert.DoesNotContain("refreshing", dialog.HeaderLine);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_QueuedOlderPublication_CannotReplaceContentOrFreshness(bool olderFailure)
    {
        using var fixture = new Fixture();
        var callbacks = new Queue<Action>();
        using var dialog = new OwnerConsoleEpicDialog(fixture.Controller, callbacks.Enqueue, CancellationToken.None);
        await dialog.OpenAsync(OwnerConsolePane.Board);
        Assert.Single(callbacks);
        callbacks.Dequeue()();
        Assert.Contains("> Alpha epic", dialog.Lines);
        fixture.Time.Current = Now.AddMinutes(1);
        fixture.Source.Epics[0] = fixture.Source.Epics[0] with { Title = "Obsolete alpha" };
        if (olderFailure) fixture.Source.Error = new InvalidOperationException("obsolete error");
        await dialog.ReloadAsync();
        Assert.Single(callbacks);
        var oldPublication = callbacks.Dequeue();

        fixture.Time.Current = Now.AddMinutes(2);
        fixture.Source.Error = null;
        fixture.Source.Epics[0] = fixture.Source.Epics[0] with { Title = "Current alpha" };
        var pending = fixture.Source.HoldNextLoad();
        var refreshing = dialog.ReloadAsync();
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        oldPublication();
        Assert.Contains("> Alpha epic", dialog.Lines);
        Assert.DoesNotContain(dialog.Lines, line => line.Contains("Obsolete alpha", StringComparison.Ordinal));
        Assert.DoesNotContain(dialog.Lines, line => line.Contains("obsolete error", StringComparison.Ordinal));
        var retainedHeader = dialog.HeaderLine.Replace("\n", " ");
        Assert.Contains("updated 01:00:00", retainedHeader);
        Assert.DoesNotContain("updated 01:01:00", retainedHeader);
        fixture.Source.Release(pending, Now.AddMinutes(2).AddHours(-24));
        await refreshing.WaitAsync(TestContext.Current.CancellationToken);
        Assert.Single(callbacks);
        callbacks.Dequeue()();
        Assert.Contains("> Current alpha", dialog.Lines);
        Assert.Contains("updated 01:02:00", dialog.HeaderLine);
        Assert.DoesNotContain("refreshing", dialog.HeaderLine);
    }
}
