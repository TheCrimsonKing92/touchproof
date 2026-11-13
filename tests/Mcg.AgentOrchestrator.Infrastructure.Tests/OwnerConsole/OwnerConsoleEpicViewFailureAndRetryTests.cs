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
public sealed class OwnerConsoleEpicViewFailureAndRetryTests
{
    [Fact]
    public async Task PlanReadFailure_ShowsReasonAndRetriesSelectedPlan()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        var prior = fixture.View.EpicView.Lines.ToArray();
        fixture.Source.PlanError = new InvalidOperationException("plan unavailable");
        await fixture.View.HandleKeyAsync(Key.Enter);
        Assert.Equal(prior, fixture.View.EpicView.Lines);
        Assert.Contains("refresh failed: plan unavailable", fixture.View.EpicView.Title.ToString());
        Assert.Contains("stale", fixture.View.EpicView.HeaderLine);
        Assert.DoesNotContain("(no plan)", fixture.View.EpicView.Lines);
        fixture.Source.PlanError = null;
        await fixture.View.HandleKeyAsync(new Key('r').WithCtrl);
        Assert.Contains("Epic plan:", fixture.View.EpicView.Lines);
        Assert.Contains("(no plan)", fixture.View.EpicView.Lines);
        Assert.Equal(["alpha", "alpha"], fixture.Source.PlanIds);
    }

    [Fact]
    public async Task OpenViewIsModalAndLoadFailureCanRecover()
    {
        using var fixture = new Fixture();
        fixture.Source.Error = new InvalidOperationException("portfolio unavailable");
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Contains("Could not load epics: portfolio unavailable. Press Ctrl+R to retry.", fixture.View.EpicView.Lines);
        foreach (var key in new[] { new Key(':'), new Key('?'), Key.Tab })
        {
            await fixture.View.HandleKeyAsync(key);
            Assert.True(key.Handled);
        }
        Assert.False(fixture.Controller.QuitRequested);
        Assert.Empty(fixture.Dialogs.Texts);
        fixture.Source.Error = null;
        fixture.View.Render(fixture.Model);
        await fixture.View.EpicView.LastLoad;
        AssertSummaries(fixture, Now.AddHours(-24));
        await fixture.View.HandleKeyAsync(new Key('q'));
        AssertClosedAndFocused(fixture.View, OwnerConsolePane.Decisions);
        Assert.False(fixture.Controller.QuitRequested);
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failure_Retry_ShowsReasonAndClearsFailureAndRetryHint(bool detail)
    {
        using var fixture = new Fixture();
        string[]? priorLines = null;
        if (detail)
        {
            await fixture.View.HandleKeyAsync(new Key('e'));
            await fixture.View.HandleKeyAsync(Key.CursorDown);
            priorLines = fixture.View.EpicView.Lines.ToArray();
        }
        fixture.Source.Error = new InvalidOperationException("portfolio unavailable.");
        await fixture.View.HandleKeyAsync(detail ? Key.Enter : new Key('e'));
        var dialog = fixture.View.EpicView;
        if (detail)
        {
            Assert.Equal(priorLines, dialog.Lines);
            Assert.Contains("refresh failed: portfolio unavailable.", dialog.Title.ToString());
            Assert.Contains("> Beta epic", dialog.Lines);
            Assert.Contains("stale", dialog.HeaderLine);
        }
        else Assert.Contains("Could not load epics: portfolio unavailable. Press Ctrl+R to retry.", dialog.Lines);
        Assert.Contains("failed", dialog.HeaderLine);
        Assert.Contains("Ctrl+R retry", dialog.HintText);
        fixture.Source.Error = null;
        var pending = fixture.Source.HoldNextLoad();
        var retrying = fixture.View.HandleKeyAsync(new Key('r').WithCtrl);
        await fixture.Source.LoadStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        if (detail)
        {
            Assert.Equal(priorLines, dialog.Lines);
            Assert.Contains("(refreshing)", dialog.Title.ToString());
            Assert.Contains("> Beta epic", dialog.Lines);
        }
        else Assert.Equal(["Loading epics (last 24 hours)…"], dialog.Lines);
        Assert.DoesNotContain("Ctrl+R retry", dialog.HintText);
        fixture.Source.Release(pending, Now.AddHours(-24));
        await retrying.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains(detail ? "Beta epic" : "> Alpha epic", dialog.Lines);
        Assert.DoesNotContain("Could not load", string.Join(" ", dialog.Lines));
        Assert.DoesNotContain("Ctrl+R retry", dialog.HintText);
        Assert.Contains("updated 01:00:00", dialog.HeaderLine);
    }

    [Fact]
    public async Task Open_MissingSource_ShowsUnavailableInsteadOfEmptyPortfolio()
    {
        using var fixture = new Fixture(wired: false);
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Equal(["Epic data is not available in this console session."], fixture.View.EpicView.Lines);
        Assert.DoesNotContain("No epics", string.Join(" ", fixture.View.EpicView.Lines));
        Assert.Contains("unavailable", fixture.View.EpicView.HeaderLine);
        Assert.DoesNotContain("Ctrl+R retry", fixture.View.EpicView.HintText);
        Assert.Equal(0, fixture.Source.Calls);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Refresh_Failure_RetainsPriorDetailAndSuccessfulTimestampUntilRetry()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.Enter);
        var prior = fixture.View.EpicView.Lines.ToArray();
        fixture.Time.Current = Now.AddMinutes(1);
        fixture.Source.Error = new InvalidOperationException("refresh unavailable");
        await fixture.View.EpicView.ReloadAsync();
        var dialog = fixture.View.EpicView;
        Assert.Equal(prior, dialog.Lines);
        Assert.Contains("refresh failed: refresh unavailable", dialog.Title.ToString());
        Assert.Contains("stale", dialog.HeaderLine);
        var retainedHeader = dialog.HeaderLine.Replace("\n", " ");
        Assert.Contains("updated 01:00:00", retainedHeader);
        Assert.DoesNotContain("updated 01:01:00", retainedHeader);
        fixture.Source.Error = null;
        await fixture.View.HandleKeyAsync(new Key('r').WithCtrl);
        Assert.Equal(prior, dialog.Lines);
        Assert.Contains("updated 01:01:00", dialog.HeaderLine);
        Assert.DoesNotContain("stale", dialog.HeaderLine);
    }
}
