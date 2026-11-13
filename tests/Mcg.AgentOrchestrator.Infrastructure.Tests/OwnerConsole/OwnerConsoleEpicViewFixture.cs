using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;

// Parallel-safe: per-test kernels, in-memory sources and an uninitialized instance application.
internal sealed class OwnerConsoleEpicViewFixture : IDisposable
{
    internal static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-08T22:00:00Z");

    internal static void AssertSummaries(OwnerConsoleEpicViewFixture fixture, DateTimeOffset? since)
    {
        foreach (var row in fixture.Source.Build(since))
        {
            // Explicit fields keep this assertion independent of the production formatter.
            var expected = $"landed {row.WindowLandedCount ?? row.LandedCount} · in flight {row.ActiveCount + row.VerifyingCount} ({row.VerifyingCount} verifying, {row.ActiveCount} active) · failed {row.WindowFailedCount ?? row.FailedCount} · backlog {row.BacklogOpenCount} open / {row.BacklogDoneCount} done";
            Assert.Contains(expected, fixture.View.EpicView.Lines);
        }
    }

    internal static void AssertClosedAndFocused(OwnerConsoleFullScreenView view, OwnerConsolePane pane)
    {
        Assert.False(view.EpicView.IsOpen);
        Assert.Equal(pane, view.FocusedPane);
        Assert.True(pane switch
        {
            OwnerConsolePane.Decisions => view.DecisionsPane.HasFocus,
            OwnerConsolePane.Board => view.BoardPane.HasFocus,
            _ => view.ActivityPane.HasFocus
        });
    }

    internal const string Plan = "Purpose: show the whole epic plan with owner decisions and phases.\nPhase one: render the plan of record with enough detail to require wrapping at the available window width while keeping every word accessible.\nNext: review with Miles. Last reviewed: 2026-10-08.";
    internal readonly OwnerConsoleHarness Harness = new();
    internal readonly OwnerConsoleEpicViewSource Source = new();
    internal readonly OwnerConsoleEpicViewDialogs Dialogs = new();
    internal readonly IApplication App = Terminal.Gui.App.Application.Create();
    internal readonly OwnerConsoleScreenController Controller;
    internal readonly OwnerConsoleFullScreenView View;
    internal readonly OwnerConsoleViewModel Model = new(new(true, 3, 0, 0, null, 0), [], [], []);
    internal readonly OwnerConsoleEpicViewClock Time = new();

    internal OwnerConsoleEpicViewFixture(bool wired = true)
    {
        Source.Epics.AddRange([
            new("alpha", "Alpha epic", null, Now, "test", Now, "test", "Alpha plan"),
            new("beta", "Beta epic", null, Now, "test", Now, "test", Plan)]);
        Add("alpha", "a-active", "Active alpha", GoalStatus.Active, -2);
        Add("alpha", "a-second", "Second active alpha", GoalStatus.WaitingForHuman, -2);
        Add("alpha", "a-verify", "Verifying alpha", GoalStatus.Verifying, -2);
        Add("alpha", "a-landed", "Landed alpha", GoalStatus.Completed, -2);
        Add("alpha", "a-weekly", "Weekly landed alpha", GoalStatus.Completed, -72);
        Add("alpha", "a-oldest", "Old landed alpha", GoalStatus.Completed, -720);
        Add("alpha", "a-failed", "Failed alpha", GoalStatus.Failed, -2);
        Add("alpha", "a-oldfail", "Old failed alpha", GoalStatus.Failed, -72);
        Add("alpha", "a-parked", "Parked alpha", GoalStatus.Parked, -720);
        Add("alpha", "a-closed", "Closed alpha", GoalStatus.Cancelled, -2);
        Add("alpha", "a-verified", "Verified alpha", GoalStatus.Verified, -2);
        Source.Members.Add(new("alpha", PortfolioMemberKind.Goal, "missing", Now, "test"));
        foreach (var (id, status) in new[] { ("todo", BacklogItemStatus.Open), ("todo2", BacklogItemStatus.Open), ("done", BacklogItemStatus.Done) })
        {
            Source.Backlog.Add(new(id, id, "body", status, Now, Now, null));
            Source.Members.Add(new("alpha", PortfolioMemberKind.BacklogItem, id, Now, "test"));
        }
        Add("beta", "b-active", "Active beta", GoalStatus.Active, -2);
        Add("beta", "b-verify", "Verifying beta", GoalStatus.Verifying, -2);
        Add("beta", "b-landed", "Landed beta", GoalStatus.Completed, -2);
        Add("beta", "b-oldest", "Old landed beta", GoalStatus.Completed, -72);
        Add("beta", "b-failed", "Failed beta", GoalStatus.Failed, -2);
        Add("beta", "b-noinfo", "Missing failed beta", GoalStatus.Failed, -2);
        Add("beta", "b-parked", "Parked beta", GoalStatus.Parked, -720);
        Harness.AddGoal("b-active", "Active beta", AgentRole.Developer);
        Harness.AddGoal("b-verify", "Verifying beta", AgentRole.Tester);
        var failed = Harness.AddGoal("b-failed", "Failed beta", AgentRole.Tester);
        Harness.Kernel.EscalateTaskFailure(failed.Id, failed.Tasks.Single().Id, "Rejected by vendor");
        var parked = Harness.AddGoal("b-parked", "Parked beta", AgentRole.Developer);
        Harness.Kernel.ParkGoal(parked.Id, "waiting on vendor");
        Source.State = Harness.State;
        Controller = OwnerConsoleControllerFactory.Create(Harness.Questions, Harness.Answers, Dialogs, Harness.State, Harness.Tail,
            Harness.Conductor, Harness.DigestReport, Harness.Digest, Time, wired ? Source : null);
        View = new(App, Controller, () => Task.CompletedTask);
        View.Render(Model);
        View.FocusDecisions();
    }

    private void Add(string epic, string id, string title, GoalStatus status, int hours)
    {
        Source.Goals.Add(new(id, status.ToString(), title, Now.AddHours(hours).ToString("O"),
            PromotionFact: status == GoalStatus.Completed ? new(id, new string('b', 40),
                new string('a', 40), new string('c', 40), Now.AddHours(hours), GoalPromotionFact.SoloProvenance) : null));
        Source.Members.Add(new(epic, PortfolioMemberKind.Goal, id, Now, "test"));
    }

    public void Dispose() { View.Dispose(); App.Dispose(); }
}
