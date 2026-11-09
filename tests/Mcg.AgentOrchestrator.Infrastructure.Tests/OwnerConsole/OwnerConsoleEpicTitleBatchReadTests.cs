using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: in-memory fakes and unique temporary portfolio stores per test.
public sealed class OwnerConsoleEpicTitleBatchReadTests
{
    [Fact]
    public async Task BuildBoard_SeveralEpics_ReadsOneBatchPerRefresh()
    {
        var harness = new OwnerConsoleHarness();
        var titles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["11111111"] = "First epic", ["22222222"] = "Second epic",
            ["33333333"] = "First epic", ["44444444"] = "Second epic", ["55555555"] = ""
        };
        foreach (var id in titles.Keys) harness.AddGoal(id, "Goal " + id, AgentRole.Developer);
        var epics = new CountingEpics(titles);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions,
            harness.Liveness, epics, harness.Clock);

        for (var refresh = 1; refresh <= 2; refresh++)
        {
            var model = await builder.BuildBoardAsync(TestContext.Current.CancellationToken);
            Assert.Equal(titles.Count, model.Board.Length);
            Assert.Equal(refresh, epics.BatchReads);
            Assert.Equal(0, epics.SingleReads);
            Assert.Equal(titles.Keys.Order(), epics.Requested.Order());
            foreach (var row in model.Board) Assert.Equal(titles[row.GoalId], row.Epic);
        }
    }

    [Fact]
    public async Task GetTitles_RealStore_MatchesPerIdLookupAndRefreshesTitles()
    {
        using var fixture = new StoreFixture();
        var store = new PortfolioStore(fixture.Path);
        var first = await store.AddEpicAsync("First epic");
        var second = await store.AddEpicAsync("Second epic");
        await store.AssignGoalToEpicAsync("11111111", first.Id);
        await store.AssignGoalToEpicAsync("22222222", second.Id);
        await store.AssignGoalToEpicAsync("33333333", first.Id);
        string[] ids = ["11111111", "22222222", "33333333", "unassigned", "UPPER"];
        await store.AssignGoalToEpicAsync("upper", second.Id);
        var lookup = new PortfolioGoalEpicLookup(fixture.Path);
        var titles = await lookup.GetTitlesAsync(ids, TestContext.Current.CancellationToken);
        foreach (var id in ids)
            Assert.Equal(await lookup.GetTitleAsync(id, TestContext.Current.CancellationToken),
                titles.GetValueOrDefault(id, ""));
        Assert.Equal("First epic", titles["11111111"]);
        Assert.Equal("Second epic", titles["22222222"]);
        Assert.False(titles.ContainsKey("UPPER"));
        await store.AssignGoalToEpicAsync("11111111", second.Id);
        var next = await lookup.GetTitlesAsync(ids, TestContext.Current.CancellationToken);
        Assert.Equal("Second epic", next["11111111"]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildBoard_MissingOrCorruptStore_ShowsEmptyTitles(bool corrupt)
    {
        using var fixture = new StoreFixture();
        if (corrupt) await File.WriteAllTextAsync(fixture.Path, "not a SQLite database");
        var harness = new OwnerConsoleHarness();
        harness.AddGoal("11111111", "First", AgentRole.Developer);
        harness.AddGoal("22222222", "Second", AgentRole.Developer);
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions,
            harness.Liveness, new PortfolioGoalEpicLookup(fixture.Path), harness.Clock);
        var model = await builder.BuildBoardAsync(TestContext.Current.CancellationToken);
        Assert.Equal(2, model.Board.Length);
        Assert.All(model.Board, row => Assert.Equal("", row.Epic));
        if (!corrupt) Assert.False(File.Exists(fixture.Path));
    }

    private sealed class CountingEpics(IReadOnlyDictionary<string, string> titles) : IOwnerGoalEpicLookup
    {
        internal int SingleReads;
        internal int BatchReads;
        internal IReadOnlyCollection<string> Requested = [];
        public Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken)
        {
            SingleReads++;
            return Task.FromResult(titles.GetValueOrDefault(goalId, ""));
        }
        public Task<IReadOnlyDictionary<string, string>> GetTitlesAsync(
            IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken)
        {
            BatchReads++;
            Requested = goalIds;
            return Task.FromResult(titles);
        }
    }

    private sealed class StoreFixture : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "owner-epic-batch-" + Guid.NewGuid().ToString("N"));
        internal string Path => System.IO.Path.Combine(_root, "portfolio.db");
        internal StoreFixture() => Directory.CreateDirectory(_root);
        public void Dispose() => Directory.Delete(_root, true);
    }
}
