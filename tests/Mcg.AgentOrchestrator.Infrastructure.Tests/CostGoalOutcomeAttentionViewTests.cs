using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: stores are fixture-local and console capture is async-context-local.
public sealed class CostGoalOutcomeAttentionViewTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly GoalId Landed = new("11111111111111111111111111111111");
    private static readonly GoalId Active = new("22222222222222222222222222222222");
    private readonly string root = CreateTempDirectory();
    private OrchestratorWorkspace Workspace => OrchestratorWorkspace.ForDirectory(root);

    [Fact]
    public async Task JsonFullAndSummaryCarryPendingMetMissedRows()
    {
        var queries = await CreateQueriesAsync();
        WriteResults(Landed);
        WriteResults(Active);

        using var full = ReadJson(queries, summary: false);
        using var summary = ReadJson(queries, summary: true);
        var fullPayload = full.RootElement.GetProperty("payload");
        var summaryPayload = summary.RootElement.GetProperty("payload");
        var rows = fullPayload.GetProperty("costGoalOutcomes");
        Assert.True(JsonElement.DeepEquals(rows, summaryPayload.GetProperty("costGoalOutcomes")));
        Assert.Collection(rows.EnumerateArray(),
            row => AssertRow(row, 0, "pending-metric", "pending", 10, null, 5, 2),
            row => AssertRow(row, 1, "met-metric", "met", 20, 10, 3, 3),
            row => AssertRow(row, 2, "missed-metric", "missed", 30, 29, 4, 3));

        Assert.False(fullPayload.TryGetProperty("costGoalOutcomeCounts", out _));
        AssertCounts(summaryPayload, pending: 1, met: 1, missed: 1, insufficientData: 0);
    }

    [Fact]
    public async Task TextAttentionPrintsPendingMetMissedRows()
    {
        var queries = await CreateQueriesAsync();
        WriteResults(Landed);
        WriteResults(Active);

        var output = ReadText(queries);
        Assert.Contains("Cost-goal outcomes:", output, StringComparison.Ordinal);
        Assert.Equal(
            new[]
            {
                $"  goal={Landed.Value} line=1 metric=pending-metric state=pending before=10 after=unavailable target=10% direction=decrease beforeSamples=5 afterSamples=2 window=3 progress=2/3",
                $"  goal={Landed.Value} line=2 metric=met-metric state=met before=20 after=10 target=10% direction=decrease beforeSamples=3 afterSamples=3 window=3",
                $"  goal={Landed.Value} line=3 metric=missed-metric state=missed before=30 after=29 target=10% direction=decrease beforeSamples=4 afterSamples=3 window=3"
            },
            OutcomeLines(output));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbsentRecordYieldsNoRowsInTextAndJson(bool summary)
    {
        var queries = await CreateQueriesAsync();
        WriteResults(Active);
        WriteResults(new GoalId("33333333333333333333333333333333"));

        using var json = ReadJson(queries, summary);
        var payload = json.RootElement.GetProperty("payload");
        Assert.False(payload.TryGetProperty("costGoalOutcomes", out _));
        Assert.False(payload.TryGetProperty("costGoalOutcomeCounts", out _));
        var output = ReadText(queries);
        Assert.DoesNotContain("Cost-goal outcomes:", output, StringComparison.Ordinal);
        Assert.Empty(OutcomeLines(output));
    }

    [Theory]
    [InlineData(false, "{")]
    [InlineData(true, "{")]
    [InlineData(false, "{}")]
    [InlineData(true, "{}")]
    public async Task UnreadableRecordYieldsOneRowInTextAndJson(bool summary, string record)
    {
        var queries = await CreateQueriesAsync();
        WriteResults(Landed);
        var path = Path.Combine(Workspace.OrchestratorDirectory, "cost-goal-outcomes", $"{Landed.Value}.json");
        File.WriteAllText(path, record);

        using var json = ReadJson(queries, summary);
        var payload = json.RootElement.GetProperty("payload");
        var row = Assert.Single(payload.GetProperty("costGoalOutcomes").EnumerateArray());
        Assert.Equal(Landed.Value, row.GetProperty("goalId").GetString());
        Assert.Equal("unreadable", row.GetProperty("state").GetString());
        Assert.True(row.GetProperty("unreadable").GetBoolean());
        Assert.Contains("unreadable", row.GetProperty("reason").GetString(), StringComparison.Ordinal);
        foreach (var property in new[] { "before", "after", "target", "beforeSampleSize", "afterSampleSize", "k", "N" })
            Assert.Equal(JsonValueKind.Null, row.GetProperty(property).ValueKind);
        if (summary)
            AssertCounts(payload, 0, 0, 0, 0);

        var textRow = Assert.Single(OutcomeLines(ReadText(queries)));
        Assert.StartsWith($"  goal={Landed.Value} state=unreadable reason=unreadable outcome record", textRow);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    private async Task<IOrchestratorStateQueries> CreateQueriesAsync()
    {
        var workspace = Workspace;
        var landedTask = new TaskSnapshot("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "Implement landed cost goal",
            AgentRole.Developer, WorkTaskStatus.Completed, null, null, null, null, null, null);
        var activeTask = new TaskSnapshot("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Implement active cost goal",
            AgentRole.Developer, WorkTaskStatus.Pending, null, null, null, null, null, null);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            [new GoalSnapshot(Landed.Value, "Landed cost goal", GoalStatus.Completed, [landedTask], [], CreatedAt: Now, TerminatedAt: Now),
             new GoalSnapshot(Active.Value, "Active cost goal", GoalStatus.Active, [activeTask], [], CreatedAt: Now)], []));
        CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        await new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);
        return SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
    }

    private void WriteResults(GoalId goalId)
    {
        var pending = new CostGoalOutcomeResult(0, new("pending-metric", "decrease", "10%", 3),
            CostGoalOutcomeState.Pending, 10, null, 5, 2, null);
        var met = new CostGoalOutcomeResult(1, new("met-metric", "decrease", "10%", 3),
            CostGoalOutcomeState.Met, 20, 10, 3, 3, null);
        var missed = new CostGoalOutcomeResult(2, new("missed-metric", "decrease", "10%", 3),
            CostGoalOutcomeState.Missed, 30, 29, 4, 3, null);
        var filing = new CostGoalOutcomeFiling(Now, null,
            [new(pending.Line, pending.Before, pending.BeforeSampleSize, null),
             new(met.Line, met.Before, met.BeforeSampleSize, null),
             new(missed.Line, missed.Before, missed.BeforeSampleSize, null)]);
        new CostGoalOutcomeStore(Workspace.OrchestratorDirectory).Write(goalId,
            new(filing, [met, missed], [], null, false) { PendingResults = [pending] });
    }

    private JsonDocument ReadJson(IOrchestratorStateQueries queries, bool summary)
    {
        string[] args = summary ? ["attention", "list", "--summary", "--json"] : ["attention", "--json"];
        return JsonDocument.Parse(CaptureConsole(() =>
            CliAttentionJsonQueryCommand.Execute(args, queries, Workspace, new FixedTime())));
    }

    private string ReadText(IOrchestratorStateQueries queries)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => Assert.True(CliAttentionQueryCommand.TryExecute(
            ["attention"], queries, Workspace, new InMemoryModelProviderRegistry([]),
            ref agents, ref profiles, ref currentGoal)));
    }

    private static string[] OutcomeLines(string output) => output.ReplaceLineEndings("\n").Split('\n')
        .Where(line => line.StartsWith("  goal=", StringComparison.Ordinal)).ToArray();

    private static void AssertRow(JsonElement row, int index, string metric, string state,
        double before, double? after, int beforeSamples, int afterSamples)
    {
        Assert.Equal(Landed.Value, row.GetProperty("goalId").GetString());
        Assert.Equal(index, row.GetProperty("line").GetInt32());
        Assert.Equal(index + 1, row.GetProperty("lineNumber").GetInt32());
        Assert.Equal(metric, row.GetProperty("metric").GetString());
        Assert.Equal(state, row.GetProperty("state").GetString());
        Assert.Equal(before, row.GetProperty("before").GetDouble());
        Assert.Equal(after, row.GetProperty("after").Deserialize<double?>());
        Assert.Equal("10%", row.GetProperty("target").GetString());
        Assert.Equal("decrease", row.GetProperty("direction").GetString());
        Assert.Equal(beforeSamples, row.GetProperty("beforeSampleSize").GetInt32());
        Assert.Equal(afterSamples, row.GetProperty("afterSampleSize").GetInt32());
        Assert.Equal(3, row.GetProperty("window").GetInt32());
        Assert.Equal(state == "pending" ? 2 : (int?)null, row.GetProperty("k").Deserialize<int?>());
        Assert.Equal(state == "pending" ? 3 : (int?)null, row.GetProperty("N").Deserialize<int?>());
        Assert.False(row.GetProperty("unreadable").GetBoolean());
    }

    private static void AssertCounts(JsonElement payload, int pending, int met, int missed, int insufficientData)
    {
        var counts = payload.GetProperty("costGoalOutcomeCounts");
        Assert.Equal(4, counts.EnumerateObject().Count());
        Assert.Equal(pending, counts.GetProperty("pending").GetInt32());
        Assert.Equal(met, counts.GetProperty("met").GetInt32());
        Assert.Equal(missed, counts.GetProperty("missed").GetInt32());
        Assert.Equal(insufficientData, counts.GetProperty("insufficientData").GetInt32());
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
