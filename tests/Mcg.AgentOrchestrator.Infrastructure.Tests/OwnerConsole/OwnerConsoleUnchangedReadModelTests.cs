using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: counted in-memory sources and markers belong to each fixture.
public sealed class OwnerConsoleUnchangedReadModelTests
{
    [Fact]
    public async Task StableTokensSkipReads_IndependentStoresInvalidateOnlyTheirDependencies()
    {
        using var f = new OwnerConsoleRefreshReuseFixture(boardReuse: true);
        async Task Read()
        {
            using var generation = f.Snapshots.BeginRefresh(reuseUnchanged: true);
            await f.Builder.BuildBoardAsync();
            await f.Builder.ReadDecisionsAsync(CancellationToken.None);
        }
        await Read();
        await Read();
        Assert.Equal((1, 1, 1), (f.State.Boards, f.EpicReads, f.Source.Reads));
        Assert.All(f.State.Loads.Values, count => Assert.Equal(1, count));
        f.Marker = "collaboration:2";
        await Read();
        Assert.Equal((1, 1, 2), (f.State.Boards, f.EpicReads, f.Source.Reads));
        f.EpicMarker = "portfolio:2";
        await Read();
        Assert.Equal((1, 2, 2), (f.State.Boards, f.EpicReads, f.Source.Reads));
        f.BoardMarker = "state:2"; // UpdatedAt deliberately unchanged.
        f.Marker = "state:2/collaboration:2";
        await Read();
        Assert.Equal((2, 2, 3), (f.State.Boards, f.EpicReads, f.Source.Reads));
        Assert.All(f.State.Loads.Values, count => Assert.Equal(2, count));
    }

    [Fact]
    public async Task OwnerRefreshForcesBoardGoalsEpicsAndQuestions()
    {
        using var f = new OwnerConsoleRefreshReuseFixture(boardReuse: true);
        var refresh = f.Coordinator();
        await refresh.RefreshAsync(skipIfBusy: true);
        await refresh.RefreshAsync(skipIfBusy: true);
        Assert.Equal((1, 1, 1), (f.State.Boards, f.EpicReads, f.Source.Reads));
        await refresh.RefreshAsync();
        Assert.Equal((2, 2, 2), (f.State.Boards, f.EpicReads, f.Source.Reads));
        Assert.All(f.State.Loads.Values, count => Assert.Equal(2, count));
        Assert.Equal(3, f.TickReads);
        Assert.Equal(3, f.GateReads);
    }

    [Fact]
    public async Task InnerQuestionCacheKeepsComprehensiveMetadataSeparateAndRetriesForcedFailure()
    {
        using var f = new OwnerConsoleRefreshReuseFixture(boardReuse: true);
        var source = new OwnerQuestionReadModel(f.State, f.Root, changeMarker: () => f.Marker);
        // No snapshots are shared with a potentially older board read.
        await f.Builder.BuildBoardAsync();
        var first = await source.ReadAsync(CancellationToken.None);
        Assert.Same(first, await source.ReadAsync(CancellationToken.None));
        Assert.Equal(2, f.State.Boards);
        Assert.Equal(1, f.State.RequestReads);
        Assert.Equal(200, f.State.Queries[0].RecentTerminalLimit);
        Assert.Null(f.State.Queries[1].RecentTerminalLimit);
        f.Marker = "updated";
        f.State.FailLoad = true;
        await Assert.ThrowsAsync<IOException>(() => source.ReadAsync(CancellationToken.None));
        f.State.FailLoad = false;
        var next = await source.ReadAsync(CancellationToken.None);
        Assert.NotSame(first, next);
        Assert.Equal(3, f.State.RequestReads);
        source.Invalidate();
        Assert.NotSame(next, await source.ReadAsync(CancellationToken.None));
        Assert.Equal(4, f.State.RequestReads);
    }

    [Fact]
    public async Task StableDecisionProjectionIsReused_ChangedAnswerabilityReplacesIt()
    {
        using var f = new OwnerConsoleRefreshReuseFixture();
        f.Source.Items.Add(new("q", "11111111", OwnerQuestionKind.HumanInput, "Question?"));
        var first = await f.Builder.ReadDecisionsAsync(CancellationToken.None);
        var second = await f.Builder.ReadDecisionsAsync(CancellationToken.None);
        Assert.True(first.Decisions == second.Decisions);
        f.Marker = "answered";
        f.Source.Items[0] = f.Source.Items[0] with { CanAnswer = false };
        var third = await f.Builder.ReadDecisionsAsync(CancellationToken.None);
        Assert.False(Assert.Single(third.Decisions).CanAnswer);
        Assert.Equal(Assert.Single(first.Decisions).Number, Assert.Single(third.Decisions).Number);
    }

    [Fact]
    public async Task FailedPortfolioReadIsRetriedEvenWhenItsTokenDidNotChange()
    {
        using var f = new OwnerConsoleRefreshReuseFixture();
        var path = Path.Combine(f.Root, "portfolio.db");
        var lookup = new PortfolioGoalEpicLookup(path);
        var builder = new OwnerConsoleViewModelBuilder(f.State, f.Source, f.Harness.Liveness, lookup, f.Clock,
            boardMarker: () => f.BoardMarker, epicMarker: () => f.EpicMarker);
        Assert.All((await builder.BuildBoardAsync()).Board, row => Assert.Equal("", row.Epic));
        Assert.False(lookup.CanReuse);
        var store = new PortfolioStore(path);
        var epic = await store.AddEpicAsync("Recovered title");
        await store.AssignGoalToEpicAsync("11111111", epic.Id);
        var recovered = await builder.BuildBoardAsync();
        Assert.Contains(recovered.Board, row => row.GoalId == "11111111" && row.Epic == "Recovered title");
        Assert.True(lookup.CanReuse);
        Assert.Equal(1, f.State.Boards);
    }

    [Fact]
    public async Task StoreCreatedAfterStartupBecomesVisibleWithoutReopeningConsole()
    {
        using var f = new OwnerConsoleRefreshReuseFixture();
        var path = Path.Combine(f.Root, "state.db");
        using var changes = new SqliteStoreChangeObservation(path);
        var state = new EmptyOwnerConsoleStateQueries(path);
        var builder = new OwnerConsoleViewModelBuilder(state, f.Source, f.Harness.Liveness, new Epics(), f.Clock,
            boardMarker: changes.Observe);
        Assert.Empty((await builder.BuildBoardAsync()).Board);
        StateDbMigrations.EnsureUpToDate(path);
        var kernel = new AgentOrchestratorKernel();
        kernel.CreateGoal(new GoalId("new-goal"), "Created after startup", [new(TaskId.New(), "Work", AgentRole.Developer)]);
        await new SqliteOrchestratorStateRepository(path).SaveAsync(kernel);
        Assert.Equal("Created after startup", Assert.Single((await builder.BuildBoardAsync()).Board).Title);
    }

    private sealed class Epics : IOwnerGoalEpicLookup
    { public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(""); }
}
