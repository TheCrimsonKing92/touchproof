using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using TestGoal = OwnerConsoleGoalChangeSummaryTestGoal;
using Queries = OwnerConsoleGoalChangeSummaryQueries;
using EmptyTail = OwnerConsoleGoalChangeSummaryEmptyTail;
using Dialogs = OwnerConsoleGoalChangeSummaryDialogs;
using static OwnerConsoleGoalChangeSummaryFixtures;
using static OwnerConsoleGoalChangeSummaryRendering;
using static OwnerConsoleGoalChangeSummaryAcceptanceJournal;

// Parallel-safe: instance-owned snapshots, I/O delegates, event gates and uninitialized GUI views.
public sealed class OwnerConsoleGoalChangeSummaryAcceptanceAndLifecycleTests
{
    [Theory]
    [InlineData("pending", "planning pending", "unavailable", false)]
    [InlineData("planned", "completed plan", "unavailable", false)]
    [InlineData("changed", "completed plan", "pending", true)]
    [InlineData("failed", "completed plan", "failed", true)]
    [InlineData("passed", "completed plan", "passed", true)]
    [InlineData("landed", "completed plan", "passed", true)]
    public async Task Lifecycle_QueryReachesSelectedPreviewAndDetail(
        string phase, string planned, string verified, bool changed)
    {
        var goal = Fixture(phase);
        var queries = new Queries(goal);
        var artifactReads = 0;
        var diffReads = 0;
        var promotionReads = 0;
        var loadEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layoutComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var summary = new OwnerGoalChangeSummaryQuery(async (id, token) =>
        {
            Assert.Equal(goal.Id.Value, id); promotionReads++;
            loadEntered.TrySetResult();
            // Without a UI dispatcher, publication runs inline on the loader thread.
            // Keep it behind this fixture's initial layout of the same preview label.
            await layoutComplete.Task.WaitAsync(token);
            return phase == "landed" ? Promotion(goal) : null;
        }, (basis, candidate, token) =>
        {
            Assert.Equal(Basis, basis); Assert.Equal(Candidate, candidate); diffReads++;
            return Task.FromResult(new CommittedGoalChanges(true, ["M src/Feature.cs", "A tests/FeatureTests.cs"], 2));
        }, (record, token) => { artifactReads++; return Task.FromResult(record.AuthoritativeStandardOutput); },
            mergeBase: (_, _) => Task.FromResult<string?>(Basis));
        var detailQuery = new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: summary);
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs, detailQuery);
        using IApplication app = Terminal.Gui.App.Application.Create();
        Assert.False(app.Initialized);
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(Model(goal));
        var previewLoad = view.PreviewLoad;
        try
        {
            await TestHangGuard.WaitAsync(loadEntered.Task, "lifecycle summary load entered");
            Assert.False(previewLoad.IsCompleted);
            Assert.Equal(string.Join('\n', "Readable goal", "Developer"), view.PreviewText);
            view.Window.Layout(new Size(100, 40));
            Assert.False(previewLoad.IsCompleted);
            Assert.Same(previewLoad, view.PreviewLoad);
        }
        finally { layoutComplete.TrySetResult(); }
        await TestHangGuard.WaitAsync(previewLoad, "lifecycle preview publication after initial layout");

        Assert.Equal(string.Join('\n', "Readable goal", changed
            ? "Changes 2 files in 2 areas: Feature.cs and 1 more" : "No committed changes yet", "Developer"), view.PreviewText);
        Assert.Equal(phase == "pending" ? 0 : changed ? 2 : 1, artifactReads);
        Assert.Equal(changed ? 1 : 0, diffReads);
        Assert.Equal(1, promotionReads);
        Assert.Equal(new[] { goal.Id.Value }, queries.ReadIds);
        Assert.Equal(0, queries.MetadataReads);

        await view.HandleKeyAsync(Key.Tab);
        await view.HandleKeyAsync(Key.Enter);
        var text = Assert.Single(dialogs.Texts);
        Assert.StartsWith("11111111  Readable goal", text);
        Assert.True(text.IndexOf("Planned:", StringComparison.Ordinal) < text.IndexOf("Tasks:", StringComparison.Ordinal));
        Assert.Contains("Planned: " + planned, text);
        Assert.Contains("Changed:", text); Assert.Contains("Reported:", text); Assert.Contains("Verified: " + verified, text);
        Assert.Contains("brief v1", text);
        Assert.Equal(changed ? 1 : 0, diffReads); // Separate immutable-range caches survive preview/detail refresh.
        if (changed)
        {
            Assert.Contains("M src/Feature.cs", text);
            Assert.Contains("worker-reported; not proof of behavior", text);
            Assert.Contains("Enable the feature", text);
            Assert.Contains(Candidate, text);
        }
        if (phase == "landed") Assert.Contains("Promotion: landed", text);
        else Assert.Contains("Promotion: landing not recorded", text);
    }

    [Theory]
    [InlineData(true, "Backlog slice: High priority: Fix the lane", "High priority: Fix the lane")]
    [InlineData(true, "Backlog slice: Backlog slice: Fix the lane", "Fix the lane")]
    [InlineData(false, "Backlog slice: Owner wording", "Backlog slice: Owner wording")]
    [InlineData(false, "Owner console: keep this colon", "Owner console: keep this colon")]
    public async Task BriefFallback_UsesReadableHeadingInSelectedPreviewAndDetail(
        bool generated, string heading, string expected)
    {
        var objective = "\r\n  " + heading + "\r\nBody" + (generated
            ? "\r\n" + BacklogIntakePlanner.TargetScopeHeadingLine + "\r\n" + BacklogIntakePlanner.UnknownScopeMarkerLine
            : "");
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(new GoalId("11111111-summary"), objective,
            [new(TaskId.New(), "Build", AgentRole.Developer)]);

        var rendered = await RenderSelectedGoalAsync(new TestGoal(kernel.ExportGoalSnapshot(goal.Id)), new());

        Assert.Equal(expected, rendered.Preview.Split('\n')[0]);
        Assert.Contains("  • " + expected + Environment.NewLine, rendered.Detail);
        Assert.Contains("  • Body" + Environment.NewLine, rendered.Detail);
        Assert.Contains("Source: authoritative brief v1", rendered.Detail);
        if (generated)
        {
            Assert.DoesNotContain(BacklogIntakePlanner.GeneratedHeadingPrefix, rendered.Preview);
            Assert.DoesNotContain(BacklogIntakePlanner.GeneratedHeadingPrefix, rendered.Detail);
        }
    }

    [Theory]
    [InlineData("passed", "passed")]
    [InlineData("gate-passed", "passed")]
    [InlineData("failed", "failed")]
    [InlineData("aborted:state-guard", "pending")]
    [InlineData("stale-candidate", "pending")]
    [InlineData("stale-main", "pending")]
    [InlineData("missing", "pending")]
    [InlineData("malformed", "pending")]
    [InlineData("oversized", "pending")]
    [InlineData("landed", "passed")]
    public async Task NormalAcceptanceWithoutObligations_ReachesSelectedPreviewAndDetail(string scenario, string expected)
    {
        var goal = Fixture(scenario == "landed" ? "landed" : "changed");
        goal = new TestGoal(goal.ToSnapshot() with { CriterionEvidenceObligations = null });
        Assert.Empty(((Goal)goal).CriterionEvidenceObligations);
        var directory = Path.Combine(Path.GetTempPath(), "mcg-summary-acceptance-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "run-events.db");
            var store = new SqliteRunEventStore(path);
            // Unrelated goals and other main/candidate identities must never establish this pass.
            await AppendAcceptanceAsync(store, goal.Id, "passed", Next, Basis);
            await AppendAcceptanceAsync(store, goal.Id, "passed", Candidate, Next);
            await AppendAcceptanceAsync(store, new GoalId("unrelated"), "passed", Candidate, Basis);
            if (scenario is not ("stale-candidate" or "stale-main" or "missing"))
            {
                await AppendAcceptanceAsync(store, goal.Id, "gate-passed", Candidate, Basis);
                if (scenario is "malformed" or "oversized")
                    await store.AppendAsync(new(RunEventTypes.GoalOperation, goal.Id.Value, "acceptance", "Failed", null,
                        scenario == "malformed" ? "{invalid" : AcceptancePayload(goal.Id, "failed", Candidate, Basis,
                            new string('x', OwnerGoalChangeSummaryQuery.MaxArtifactBytes + 1)), At.AddMinutes(4)), Token);
                else if (scenario != "landed")
                    await AppendAcceptanceAsync(store, goal.Id, scenario, Candidate, Basis);
            }
            var reads = 0;
            var layoutComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var summary = new OwnerGoalChangeSummaryQuery(
                promotion: (_, _) => Task.FromResult<GoalPromotionFact?>(scenario == "landed" ? Promotion(goal) : null),
                acceptance: async (id, candidate, landed, token) =>
                {
                    // This uninitialized app publishes inline on the loader thread. Finish the
                    // fixture's initial layout before allowing it to write the same preview label.
                    await layoutComplete.Task.WaitAsync(token);
                    reads++;
                    Assert.Equal(goal.Id, id);
                    Assert.Equal(Candidate, candidate);
                    if (scenario == "landed") Assert.Equal(Basis, landed!.PriorMainRevision);
                    return new OwnerGoalAcceptanceEvidence(Basis,
                        await GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(path, id, candidate, Basis, token));
                });
            var queries = new Queries(goal);
            var detailQuery = new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: summary);
            var harness = new OwnerConsoleHarness();
            var dialogs = new Dialogs();
            using var controller = Controller(harness, dialogs, detailQuery);
            using IApplication app = Terminal.Gui.App.Application.Create();
            using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
            view.Render(Model(goal));
            try { view.Window.Layout(new Size(100, 40)); }
            finally { layoutComplete.TrySetResult(); }
            await view.PreviewLoad.WaitAsync(Token);
            if (expected is "passed" or "failed") Assert.Contains("gate " + expected, view.PreviewText);
            else Assert.DoesNotContain(" · ", view.PreviewText);
            await view.HandleKeyAsync(Key.Tab);
            await view.HandleKeyAsync(Key.Enter);
            var detail = Assert.Single(dialogs.Texts);
            Assert.Contains("Verified: " + expected, detail);
            Assert.Equal(2, reads);
            Assert.Equal(new[] { goal.Id.Value, goal.Id.Value }, queries.ReadIds);
            Assert.Equal(0, queries.MetadataReads);
            if (expected == "passed")
            {
                Assert.Contains("applicable full acceptance", detail);
                Assert.Contains("event ", detail);
                Assert.Contains("main " + Basis, detail);
            }
            else Assert.DoesNotContain("Verified: passed", detail);
            if (scenario is "malformed" or "oversized") Assert.Contains("Acceptance evidence unavailable", detail);
            Assert.Contains(scenario == "landed" ? "Promotion: landed" : "Promotion: landing not recorded", detail);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task NormalAcceptanceReceiptUpdates_InvalidatePassAtSameCandidate()
    {
        var goal = Fixture("changed");
        Assert.Empty(((Goal)goal).CriterionEvidenceObligations);
        var directory = Path.Combine(Path.GetTempPath(), "mcg-summary-receipt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "run-events.db");
            var store = new SqliteRunEventStore(path);
            var summary = new OwnerGoalChangeSummaryQuery(acceptance: async (id, candidate, _, token) =>
                new OwnerGoalAcceptanceEvidence(Basis,
                    await GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(path, id, candidate, Basis, token)));
            var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: summary);
            async Task AssertRenderedAsync(string state)
            {
                var detail = await query.LoadAsync(goal.Id.Value, Token);
                var output = new OwnerConsoleCapturedOutput();
                OwnerConsoleGoalDetailFormatter.Render(detail!, output, TimeZoneInfo.Utc);
                Assert.Contains("Verified: " + state, output.Text);
            }
            await AppendAcceptanceAsync(store, goal.Id, "gate-passed", Candidate, Basis);
            await AssertRenderedAsync("passed");
            // Equal timestamps exercise the journal's append-order tiebreak, without sleeps.
            await AppendAcceptanceAsync(store, goal.Id, "failed", Candidate, Basis);
            await AssertRenderedAsync("failed");
            await AppendAcceptanceAsync(store, goal.Id, "aborted:state-guard", Candidate, Basis);
            await AssertRenderedAsync("pending");
            await AppendAcceptanceAsync(store, goal.Id, "passed", Candidate, Basis);
            await AssertRenderedAsync("passed");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
