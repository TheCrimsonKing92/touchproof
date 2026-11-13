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
public sealed class OwnerConsoleEpicViewPlanAndDetailTests
{
    [Fact]
    public async Task Detail_LaterUpdateAndUnknownDate_MatchOriginalPromotionRollup()
    {
        using var fixture = new Fixture();
        var old = fixture.Source.Goals.Single(goal => goal.Id == "b-oldest");
        fixture.Source.Goals.Remove(old);
        fixture.Source.Goals.Add(old with { UpdatedAt = Now.ToString("O") });
        var known = fixture.Source.Goals.Single(goal => goal.Id == "b-landed");
        fixture.Source.Goals.Remove(known);
        fixture.Source.Goals.Add(known with { PromotionFact = null });
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        await fixture.View.HandleKeyAsync(Key.Enter);
        var row = fixture.Source.Build(Now.AddHours(-24)).Single(row => row.Epic.Id == "beta");
        Assert.Equal(0, row.WindowLandedCount);
        Assert.Equal(1, row.UnknownLandingDateCount);
        Assert.Contains("Unknown landing dates:", fixture.View.EpicView.Lines);
        Assert.Contains(fixture.View.EpicView.Lines, line => line.Contains("Original promotion date: unknown", StringComparison.Ordinal));
        Assert.DoesNotContain(fixture.View.EpicView.Lines, line => line.Contains("Old landed beta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task EnterOpensPlanInFlightLandedAndTrouble()
    {
        using var fixture = new Fixture();
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        await fixture.View.HandleKeyAsync(Key.Enter);
        var dialog = fixture.View.EpicView;

        Assert.True(dialog.ShowingDetail);
        Assert.Equal("beta", dialog.SelectedEpicId);
        var lines = dialog.Lines;
        var plan = lines.Skip(2).TakeWhile(line => line != "Epic plan:").Where(line => line.Length > 0);
        Assert.Equal(Fixture.Plan.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries),
            string.Join(" ", plan).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        Assert.Contains("b-active  Active beta  Developer", lines);
        Assert.Contains("b-verify  Verifying beta  Tester", lines);
        Assert.Contains("b-landed  Landed beta  -", lines);
        Assert.DoesNotContain(lines, line => line.Contains("Old landed beta", StringComparison.Ordinal));
        Assert.Contains("Rejected by vendor", lines);
        Assert.Contains("Goal parked: waiting on vendor", lines);
        Assert.Contains("no reason recorded", lines);
        var row = fixture.Source.Build(Now.AddHours(-24)).Single(item => item.Epic.Id == "beta");
        Assert.Equal(row.WindowLandedCount, Section(lines, "Landed in window:").Count(line => line.StartsWith("b-", StringComparison.Ordinal)));
        Assert.Equal(row.WindowFailedCount, Section(lines, "Failed:").Count(line => line.StartsWith("b-", StringComparison.Ordinal)));

        await fixture.View.HandleKeyAsync(Key.Esc);
        Assert.True(dialog.IsOpen);
        Assert.False(dialog.ShowingDetail);
        Assert.Equal("beta", dialog.SelectedEpicId);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task Enter_HeldPlanRead_ShowsSelectedEpicPlanThroughSource()
    {
        using var fixture = new Fixture();
        var item = new EpicPlanItemStatus(new(1, EpicPlanItemKind.Step, null, "Review with owner", false),
            null, "Review with owner");
        var plan = new EpicPlanView(new("beta", "Owner can read the plan", [item.Item], []), [item], []);
        fixture.Source.Plans.Add("beta", plan);
        await fixture.View.HandleKeyAsync(new Key('e'));
        Assert.Equal(0, fixture.Source.PlanCalls);
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        var pending = fixture.Source.PendingPlan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var entering = fixture.View.HandleKeyAsync(Key.Enter);
        // PlanStarted synchronizes the read; the timeout only detects a plan read that never finishes.
        await fixture.Source.PlanStarted.Task.WaitAsync(TestContext.Current.CancellationToken);

        var dialog = fixture.View.EpicView;
        Assert.Contains("> Beta epic", dialog.Lines);
        Assert.Contains("requested Beta epic", dialog.HeaderLine.Replace("\n", " "));
        Assert.Equal(1, fixture.Source.PlanCalls);
        Assert.Equal(["beta"], fixture.Source.PlanIds);
        Assert.False(entering.IsCompleted);
        fixture.Source.PendingPlan = null;
        pending.SetResult(plan);
        await entering.WaitAsync(TestContext.Current.CancellationToken);

        Assert.Contains("Epic plan:", dialog.Lines);
        Assert.Contains("Bar: Owner can read the plan", dialog.Lines);
        Assert.Contains("1. Review with owner — open", dialog.Lines);
        Assert.Contains("Next step: " + EpicPlanNextStep.Compute(plan.Items).Text, dialog.Lines);
        Assert.True(Array.IndexOf(dialog.Lines.ToArray(), "Epic plan:") < Array.IndexOf(dialog.Lines.ToArray(), "In flight:"));
    }

    [Fact]
    public async Task DetailWrapsOnResizeAndPagesThroughFullPlan()
    {
        using var fixture = new Fixture();
        fixture.View.Window.Layout(new System.Drawing.Size(46, 18));
        await fixture.View.HandleKeyAsync(new Key('e'));
        await fixture.View.HandleKeyAsync(Key.CursorDown);
        await fixture.View.HandleKeyAsync(Key.Enter);
        fixture.View.Window.Layout(new System.Drawing.Size(46, 18));
        var dialog = fixture.View.EpicView;
        Assert.True(dialog.BodyPane.Viewport.Width > 0);
        Assert.All(dialog.Lines, line => Assert.True(line.GetColumns() <= dialog.BodyPane.Viewport.Width, line));
        var narrowPlanLines = dialog.Lines.TakeWhile(line => line != "Epic plan:").Count();
        await fixture.View.HandleKeyAsync(Key.PageDown);
        Assert.True(dialog.BodyPane.SelectedItem > 0);
        await fixture.View.HandleKeyAsync(Key.PageUp);
        Assert.Equal(0, dialog.BodyPane.SelectedItem);
        fixture.View.Window.Layout(new System.Drawing.Size(90, 18));
        Assert.True(dialog.Lines.TakeWhile(line => line != "Epic plan:").Count() < narrowPlanLines);
        Assert.All(dialog.Lines, line => Assert.True(line.GetColumns() <= dialog.BodyPane.Viewport.Width, line));
    }

    private static IEnumerable<string> Section(IReadOnlyList<string> lines, string heading) =>
        lines.SkipWhile(line => line != heading).Skip(1).TakeWhile(line => !line.EndsWith(':'));
}
