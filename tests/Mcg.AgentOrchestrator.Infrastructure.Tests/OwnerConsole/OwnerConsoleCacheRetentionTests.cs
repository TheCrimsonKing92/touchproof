using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns sources, clock and uniquely rooted SQLite stores.
public sealed class OwnerConsoleCacheRetentionTests
{
    [Fact]
    public async Task RepeatedCreateUpdateRemoveReturnsCachesToLiveSetAndBoundedActivityHistory()
    {
        using var f = new OwnerConsoleRefreshReuseFixture(boardReuse: true);
        for (var round = 0; round < 130; round++)
        {
            var id = $"goal-{round:0000}";
            f.Harness.AddGoal(id, id, AgentRole.Developer);
            f.State.Visible.Clear();
            f.State.Visible.Add(id);
            f.State.Versions[id] = "unchanged-timestamp";
            f.BoardMarker = f.Marker = round.ToString();
            f.Source.Items.Clear();
            f.Source.Items.Add(new("question-" + round, id, OwnerQuestionKind.HumanInput, "Question " + round));
            using var generation = f.Snapshots.BeginRefresh(reuseUnchanged: true);
            var board = await f.Builder.BuildBoardAsync();
            var decisions = await f.Builder.ReadDecisionsAsync(CancellationToken.None);
            Assert.Equal(id, Assert.Single(board.Board).GoalId);
            Assert.Equal(round + 1, Assert.Single(decisions.Decisions).Number);
            Assert.Equal(1, f.Builder.Retained.Numbers);
            Assert.Equal(1, f.Builder.Retained.Holds);
            Assert.InRange(f.Builder.Retained.Goals, 1, 101);
            Assert.Equal(f.Builder.Retained.Goals, f.Builder.Retained.Roles);
        }
        f.State.Visible.Clear();
        f.Source.Items.Clear();
        f.BoardMarker = f.Marker = "removed";
        Assert.Empty((await f.Builder.BuildBoardAsync()).Board);
        Assert.Empty((await f.Builder.ReadDecisionsAsync(CancellationToken.None)).Decisions);
        Assert.Equal(0, f.Builder.Retained.Numbers);
        Assert.Equal(0, f.Builder.Retained.Holds);
        Assert.InRange(f.Builder.Retained.Goals, 0, 100);
    }

    [Fact]
    public async Task ProjectSwitchesWithCollidingIdsReleaseAbandonedOwnersAndDispose()
    {
        var root = Directory.CreateTempSubdirectory("owner-project-cache-").FullName;
        try
        {
            var clock = new OwnerConsoleTestClock();
            var projects = new List<OwnerConsoleBoardProject>();
            foreach (var name in new[] { "alpha", "beta" })
            {
                var directory = Path.Combine(root, name);
                Directory.CreateDirectory(directory);
                var path = Path.Combine(directory, "state.db");
                StateDbMigrations.EnsureUpToDate(path);
                var kernel = new AgentOrchestratorKernel();
                kernel.CreateGoal(new GoalId("same-goal"), name + " title", [new(TaskId.New(), "Work", AgentRole.Developer)]);
                await new SqliteOrchestratorStateRepository(path).SaveAsync(kernel, TestContext.Current.CancellationToken);
                projects.Add(new(name, path, Path.Combine(directory, "portfolio.db"), OrchestratorDirectory: directory));
            }
            using var source = new OwnerConsoleBoardSource("launch", OwnerConsoleProjectScope.All, () => projects, clock);
            var own = new OwnerConsoleViewModel(new(false, 0, 0, 0, null, 0), [], [], []);
            for (var round = 0; round < 20; round++)
            {
                var name = round % 2 == 0 ? "alpha" : "beta";
                Assert.True(source.HandleScope(name).Success);
                var model = await source.ComposeAsync(own);
                var row = Assert.Single(model.Board.Where(row => row.GoalId == "same-goal"));
                Assert.Equal(name, row.Project);
                Assert.Equal(name + " title", row.Title);
                Assert.Equal(1, source.RetainedProjects);
            }
            source.HandleScope("all");
            projects.Clear();
            await source.ComposeAsync(own);
            Assert.Equal(0, source.RetainedProjects);
            source.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => source.ComposeAsync(own));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Fact]
    public async Task FailedAndCanceledBoardReadsAndUnknownTokensNeverBecomeReusable()
    {
        using var f = new OwnerConsoleRefreshReuseFixture(boardReuse: true);
        f.State.FailLoad = true;
        await Assert.ThrowsAsync<IOException>(() => f.Builder.BuildBoardAsync());
        f.State.FailLoad = false;
        await f.Builder.BuildBoardAsync();
        await f.Builder.BuildBoardAsync();
        Assert.Equal(2, f.State.Boards);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Builder.BuildBoardAsync(canceled.Token, force: true));
        f.BoardMarker = null;
        await f.Builder.BuildBoardAsync();
        await f.Builder.BuildBoardAsync();
        Assert.Equal(4, f.State.Boards);
        Assert.All(f.State.Loads.Values, count => Assert.Equal(3, count));
    }
}
