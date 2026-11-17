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

// Parallel-safe: instance-owned snapshots, I/O delegates, event gates and uninitialized GUI views.
public sealed class OwnerConsoleGoalChangeSummaryBoundsCancellationRefreshAndViewsTests
{
    [Fact]
    public async Task Bounds_LimitFilesBulletsAndArtifactReads()
    {
        var goal = Fixture("changed");
        var reads = 0;
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: new(changes: (_, _, _) =>
            Task.FromResult(new CommittedGoalChanges(true, Enumerable.Range(0, 100).Select(i => $"M src/{i}.cs").ToArray(), 150)),
            artifact: (record, token) => { reads++; return Task.FromResult(record.AuthoritativeStandardOutput); }));
        var result = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.Equal(20, result.Changed.Items.Count);
        Assert.Equal(130, result.Changed.Omitted);
        Assert.InRange(result.Planned.Items.Count, 1, 5);
        Assert.Equal(2, reads);
        Assert.Contains("130 more", string.Join('\n', result.DetailLines()));
    }

    [Fact]
    public async Task SixMappedDeliverables_ShowFiveAndAnOmission()
    {
        var goal = Fixture("planned");
        var criteria = Enumerable.Range(1, 6).Select(index => $"Deliver behavior {index}").ToArray();
        var spec = new RefinedSpec("Expected feature", criteria, VerificationClass.TestVerifiable, [], []);
        var snapshot = goal.ToSnapshot();
        goal = new TestGoal(snapshot with
        {
            RefinedSpecVersions = [snapshot.RefinedSpecVersions![0] with { Spec = snapshot.RefinedSpecVersions[0].Spec with { AcceptanceCriteria = criteria } }],
            Tasks = snapshot.Tasks.Select(task => task.RequiredRole != AgentRole.Planner ? task : task with
                { LastVerification = task.LastVerification! with { AcceptanceCriteriaVersionHash = EffectiveAcceptanceCriteriaVersion.ComputeHash(spec, []) } }).ToArray()
        });
        var plan = "<!-- MCG_DURABLE_PLANNER_PLAN:BEGIN -->\n## Acceptance criteria mapping\n" +
            string.Join('\n', criteria.Select((criterion, index) => $"{index + 1}. disposition=planned; plan={criterion}; owner=Acceptance")) +
            "\n## Target seams and symbols\nsrc/Feature.cs\n<!-- MCG_DURABLE_PLANNER_PLAN:END -->";
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: new(artifact: (_, _) => Task.FromResult<string?>(plan)));
        var result = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.Equal("completed plan", result.Planned.State);
        Assert.Equal(5, result.Planned.Items.Count);
        Assert.Equal(1, result.Planned.Omitted);
        Assert.Contains("criteria 5", result.Planned.Items.Last());
    }

    [Fact]
    public async Task ArtifactCancellation_StopsBeforeDiffAndForwardsTheToken()
    {
        var goal = Fixture("changed");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var diffReads = 0;
        CancellationToken received = default;
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: new(
            changes: (_, _, _) => { diffReads++; return Task.FromResult(new CommittedGoalChanges(true, [], 0)); },
            artifact: (_, token) => { received = token; entered.SetResult(); return release.Task; }));
        var load = query.LoadSummaryAsync(goal.Id.Value, cancel.Token);
        await entered.Task.WaitAsync(Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load);
        Assert.True(received.IsCancellationRequested);
        Assert.Equal(0, diffReads);
        release.SetResult(Plan);
    }

    [Fact]
    public async Task DetailRefreshFailure_MarksPriorSummaryStale()
    {
        var goal = Fixture("changed");
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail());
        var output = new OwnerConsoleCapturedOutput();
        OwnerConsoleGoalDetailFormatter.Render((await query.LoadAsync(goal.Id.Value, Token))!, output, TimeZoneInfo.Utc);
        var harness = new OwnerConsoleHarness();
        using var controller = Controller(harness, new Dialogs(), query);
        using var dialog = new OwnerConsoleGoalDialog(controller, new(output.Text, [], [], null, null, null),
            _ => Task.FromException<OwnerConsoleGoalDialog.Content?>(new IOException("offline")),
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, Token);
        var priorLines = dialog.Page.Lines.ToArray();
        dialog.Start(action => action());
        controller.Apply(Model(goal));
        await dialog.LastLoad.WaitAsync(Token);
        Assert.Equal(priorLines, dialog.Page.Lines);
        Assert.Equal("refresh failed: offline", dialog.TitleStatus);
    }

    [Fact]
    public async Task Selection_CancelsAndRejectsLateResultsAndDisposal()
    {
        var firstEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<OwnerGoalChangeSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var posts = new System.Collections.Concurrent.ConcurrentQueue<Action>();
        var posted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<OwnerConsoleSelectedGoalPreview>();
        CancellationToken firstToken = default;
        using var session = new OwnerConsoleSelectedGoalSummary((id, token) =>
        {
            if (id == "first") { firstToken = token; firstEntered.SetResult(); return first.Task; }
            return Task.FromResult<OwnerGoalChangeSummary?>(Summary(id));
        }, action => { posts.Enqueue(action); posted.TrySetResult(); }, applied.Add, Token);
        session.Select("first");
        await firstEntered.Task.WaitAsync(Token);
        session.Select("second");
        await posted.Task.WaitAsync(Token);
        Assert.True(firstToken.IsCancellationRequested);
        Assert.True(posts.TryDequeue(out var post)); post();
        await session.LastLoad.WaitAsync(Token);
        first.SetResult(Summary("first"));
        Assert.Equal("second", applied.Last().Summary!.GoalId);
        session.Select("third");
        var loading = session.LastLoad;
        session.Dispose();
        while (posts.TryDequeue(out var queued)) queued();
        await loading.WaitAsync(Token);
        Assert.DoesNotContain(applied, item => item.Summary?.GoalId == "third");
    }

    [Fact]
    public async Task RefreshDuringLoad_CoalescesAndMarksInterimContentStale()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var staleApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new TaskCompletionSource<OwnerGoalChangeSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var next = new TaskCompletionSource<OwnerGoalChangeSummary?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new List<OwnerConsoleSelectedGoalPreview>();
        CancellationToken firstToken = default;
        var reads = 0;
        using var session = new OwnerConsoleSelectedGoalSummary((id, token) =>
        {
            if (++reads == 1) { firstToken = token; entered.SetResult(); return first.Task; }
            nextEntered.SetResult(); return next.Task;
        }, action => action(), preview =>
        {
            applied.Add(preview);
            if (preview.Summary?.Planned.State.StartsWith("[stale", StringComparison.Ordinal) == true)
                staleApplied.TrySetResult();
        }, Token);
        session.Select("selected");
        await entered.Task.WaitAsync(Token);
        session.Select("selected", refresh: true);
        session.Select("selected", refresh: true);
        Assert.False(firstToken.IsCancellationRequested);
        first.SetResult(Summary("selected"));
        await staleApplied.Task.WaitAsync(Token);
        await nextEntered.Task.WaitAsync(Token);
        Assert.StartsWith("[stale", applied.Last().Summary!.Verified.State);
        next.SetResult(Summary("selected"));
        await session.LastLoad.WaitAsync(Token);
        Assert.Equal(2, reads);
        Assert.Equal("pending", applied.Last().Summary!.Verified.State);
    }

    [Theory]
    [InlineData(80, 25)]
    [InlineData(100, 40)]
    public async Task ProductionViews_ResizeScrollNavigateAndReachCloseEpic(int width, int height)
    {
        var goal = Fixture("changed");
        var queries = new Queries(goal);
        var longPath = "M src/" + string.Concat(Enumerable.Repeat("長い😀/", 150)) + "Feature.cs";
        var query = new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: new(changes: (_, _, _) =>
            Task.FromResult(new CommittedGoalChanges(true, [longPath], 1)),
            mergeBase: (_, _) => Task.FromResult<string?>(Basis)));
        var harness = new OwnerConsoleHarness();
        var dialogs = new Dialogs();
        using var controller = Controller(harness, dialogs, query);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = new OwnerConsoleFullScreenView(app, controller, () => Task.CompletedTask);
        view.Render(Model(goal)); view.Window.Layout(new Size(width, height));
        await view.PreviewLoad.WaitAsync(Token);
        Assert.Contains("Readable goal", view.PreviewText); Assert.Contains("Changes 1 file", view.PreviewText);
        Assert.All(view.PreviewText.Split('\n'), line => Assert.True(line.GetColumns() <= width - 2));
        var reads = queries.ReadIds.Count;
        view.Window.Layout(new Size(width == 80 ? 100 : 80, height == 25 ? 40 : 25));
        Assert.Equal(reads, queries.ReadIds.Count);
        await view.HandleKeyAsync(Key.Tab); await view.HandleKeyAsync(Key.Enter);
        var detailText = Assert.Single(dialogs.Texts);
        Assert.Contains(longPath, detailText);
        using var goalDialog = new OwnerConsoleGoalDialog(controller,
            new(detailText, [], [], null, null, "epic"), _ => Task.FromResult<OwnerConsoleGoalDialog.Content?>(null),
            _ => Task.CompletedTask, (_, _) => Task.CompletedTask, Token);
        var closed = 0;
        var production = goalDialog.CreateView(() => closed++, key => goalDialog.HandleKeyAsync(key));
        using var dialog = production.Dialog;
        foreach (var size in new[] { new Size(width, height), new Size(80, 25), new Size(100, 40) })
        {
            dialog.Layout(size); dialog.Render();
            Assert.True(dialog.Body.Viewport.Height > 0);
            Assert.All(dialog.Buttons.Where(button => button.Visible), button =>
            {
                Assert.True(button.Frame.X >= 0);
                Assert.True(button.Frame.Right <= button.SuperView!.Viewport.Width);
                Assert.True(button.Frame.Bottom <= button.SuperView.Viewport.Height);
            });
            Assert.Contains("Epic (e)", production.Buttons['e'].Text);
            // Exercise the production page's supported keys through the entire expanded text.
            for (var step = 0; step < goalDialog.Page.Lines.Count && goalDialog.Page.Selected < goalDialog.Page.Lines.Count - 1; step++)
                Assert.True(dialog.HandleKey(new Key(Key.PageDown)));
            Assert.Equal(goalDialog.Page.Lines.Count - 1, goalDialog.Page.Selected);
            Assert.True(goalDialog.Page.Offset > 0);
            Assert.Equal(goalDialog.Page.Lines.Last(), goalDialog.Page.Visible.Last());
            for (var step = 0; step < goalDialog.Page.Lines.Count && goalDialog.Page.Selected > 0; step++)
                Assert.True(dialog.HandleKey(new Key(Key.PageUp)));
            Assert.Equal(0, goalDialog.Page.Selected);
            Assert.Equal(0, goalDialog.Page.Offset);
        }
        production.Buttons['e'].InvokeCommand(Command.Accept);
        Assert.Equal("epic", goalDialog.RequestedEpicId);
        dialog.Buttons.Single(button => button.Text == "Close").InvokeCommand(Command.Accept);
        Assert.Equal(1, closed);
        Assert.True(await goalDialog.HandleKeyAsync('q'));
        Assert.Empty(harness.Answers.Calls); Assert.Empty(harness.Conductor.Calls);
    }
}
