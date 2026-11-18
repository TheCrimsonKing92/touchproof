using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its temporary backlog, outcome records and conduct log.
public sealed class CostGoalOutcomeVerdictApplicationTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("cost-goal-outcome-").FullName;
    private static readonly DateTimeOffset FiledAt = new(2026, 10, 31, 0, 0, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly OutcomeMetricLine Selection = new("selection-milliseconds", "decrease", "20%", 3);
    private static readonly OutcomeMetricLine Classes = new("executed-class-count", "decrease", "15", 3);
    private static readonly OutcomeMetricLine Rate = new("full-gate-rate", "decrease", ".5", 3);

    [Theory]
    [InlineData(BacklogItemStatus.Open)]
    [InlineData(BacklogItemStatus.Superseded)]
    public async Task Apply_MissedWithActionableSource_ReopensAndAddsOneNote(BacklogItemStatus initialStatus)
    {
        var (store, backlog, writer) = OpenStores();
        var source = await backlog.AddAsync("Reduce gate cost");
        if (initialStatus == BacklogItemStatus.Superseded)
            await backlog.SupersedeAsync(source.Id, (await backlog.AddAsync("Replacement")).Id);
        var goal = RecordFiling(store, source.Id);
        var results = Results();

        CostGoalOutcomeVerdict.Apply(goal, results, store, backlog, writer);
        var revised = results.Select(result => result with { State = CostGoalOutcomeState.Met, After = 5 }).ToArray();
        CostGoalOutcomeVerdict.Apply(goal, revised, store, backlog, writer);

        var updated = (await backlog.GetByExactIdAsync(source.Id))!;
        Assert.Equal(BacklogItemStatus.Open, updated.Status);
        Assert.Null(updated.SupersededBy);
        var note = Assert.Single(updated.Notes).Text;
        Assert.Contains("metric=selection-milliseconds verdict=missed before=100 after=91 target=20%", note);
        Assert.Contains("metric=executed-class-count verdict=met before=20 after=12 target=15", note);
        Assert.Contains("metric=full-gate-rate verdict=insufficient-data before=0.6 after=unavailable target=.5", note);
        Assert.Equal(results, store.Read(goal)!.FinalVerdicts.ToArray());
        Assert.Collection(ReadEvents(writer),
            row => AssertEvent(row, goal, "selection-milliseconds verdict=missed before=100 after=91 target=20%"),
            row => AssertEvent(row, goal, "executed-class-count verdict=met before=20 after=12 target=15"),
            row => AssertEvent(row, goal, "full-gate-rate verdict=insufficient-data before=0.6 after=unavailable target=.5"));
    }

    [Fact]
    public async Task Apply_DoneSourceAndTwoMisses_CreatesOneLinkedFollowUp()
    {
        var (store, backlog, writer) = OpenStores();
        var source = await backlog.AddAsync("Reduce gate cost");
        await backlog.CloseAsync(source.Id);
        var goal = RecordFiling(store, source.Id);
        var results = Results();
        results[1] = results[1] with { State = CostGoalOutcomeState.Missed, After = 16 };

        CostGoalOutcomeVerdict.Apply(goal, results, store, backlog, writer);
        CostGoalOutcomeVerdict.Apply(goal, results, store, backlog, writer);

        var updated = (await backlog.GetByExactIdAsync(source.Id))!;
        Assert.Equal(BacklogItemStatus.Done, updated.Status);
        Assert.Empty(updated.Notes);
        var followUp = Assert.Single(await backlog.ListAsync());
        Assert.Equal(goal.Value, followUp.SourceGoalId);
        Assert.Contains($"Source backlog item: {source.Id}", followUp.Body);
        Assert.Contains("metric=selection-milliseconds verdict=missed before=100 after=91 target=20%", followUp.Body);
        Assert.Contains("metric=executed-class-count verdict=missed before=20 after=16 target=15", followUp.Body);
        var link = Assert.Single(updated.Links);
        Assert.Equal(BacklogLinkKind.Related, link.Kind);
        Assert.Equal(followUp.Id, link.OtherId(source.Id));
        Assert.Equal(3, ReadEvents(writer).Length);
    }

    [Fact]
    public async Task Apply_Pending_DoesNotStoreOrEmitVerdicts()
    {
        var (store, backlog, writer) = OpenStores();
        var source = await backlog.AddAsync("Reduce gate cost");
        var goal = RecordFiling(store, source.Id);
        var pending = Results().Select(result => result with { State = CostGoalOutcomeState.Pending, After = null }).ToArray();

        CostGoalOutcomeVerdict.Apply(goal, pending, store, backlog, writer);

        Assert.Empty(store.Read(goal)!.FinalVerdicts);
        Assert.Empty((await backlog.GetByExactIdAsync(source.Id))!.Notes);
        Assert.False(File.Exists(writer.CurrentPath));
    }

    [Fact]
    public async Task Apply_MissingSource_ReportsExactItemAndLeavesOtherItemsAlone()
    {
        var (store, backlog, writer) = OpenStores();
        var other = await backlog.AddAsync("Unrelated");
        var missing = Guid.NewGuid().ToString("N");
        var goal = RecordFiling(store, missing);

        var error = Assert.Throws<InvalidOperationException>(() =>
            CostGoalOutcomeVerdict.Apply(goal, Results(), store, backlog, writer));

        Assert.Contains(missing, error.Message);
        Assert.False(store.Read(goal)!.BacklogApplied);
        Assert.Empty((await backlog.GetByExactIdAsync(other.Id))!.Notes);
        Assert.Equal(3, ReadEvents(writer).Length);
    }

    private (CostGoalOutcomeStore, BacklogStore, ConductEventLogWriter) OpenStores() =>
        (new(root), new(Path.Combine(root, "backlog.db")), new(Path.Combine(root, "conduct-events.log")));

    private static GoalId RecordFiling(CostGoalOutcomeStore store, string source)
    {
        var goal = GoalId.New();
        store.RecordFiling(goal, new(FiledAt, source, [new(Selection, 100, 3, null),
            new(Classes, 20, 3, null), new(Rate, .6, 3, null)]));
        return goal;
    }

    private static CostGoalOutcomeResult[] Results() =>
        [new(0, Selection, CostGoalOutcomeState.Missed, 100, 91, 3, 3, null),
         new(1, Classes, CostGoalOutcomeState.Met, 20, 12, 3, 3, null),
         new(2, Rate, CostGoalOutcomeState.InsufficientData, .6, null, 3, 2, "window-unfilled")];

    private static ConductEventRecord[] ReadEvents(ConductEventLogWriter writer) => File.ReadLines(writer.CurrentPath)
        .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, Json)!).ToArray();

    private static void AssertEvent(ConductEventRecord row, GoalId goal, string evidence)
    {
        Assert.Equal("cost-goal-outcome-verdict", row.EventKind);
        Assert.Equal(goal.Value, row.GoalId);
        Assert.StartsWith($"OUTCOME_VERDICT goal={goal}", row.Detail);
        Assert.Contains(evidence, row.Detail);
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(root);
}
