using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

public sealed class OwnerConsoleCostGoalOutcomeTests : IDisposable
{
    private const string GoalIdWithRecord = "11111111111111111111111111111111";
    private readonly string _directory = SharedTestSupport.CreateTempDirectory();
    private readonly OwnerConsoleHarness _harness = new();

    [Fact]
    public async Task DetailRendersEveryOutcomeStateAndStructuredFields()
    {
        var goal = _harness.AddGoal(GoalIdWithRecord, "Cost goal", AgentRole.Developer);
        WriteRecord(goal.Id,
            new(0, new("pending-metric", "decrease", "10%", 3), CostGoalOutcomeState.Pending, 10.5, null, 5, 2, null),
            new(1, new("met-metric", "increase", "10%", 3), CostGoalOutcomeState.Met, 20, 30, 3, 3, null),
            new(2, new("missed-metric", "decrease", "10%", 3), CostGoalOutcomeState.Missed, 30, 29, 4, 3, null),
            new(3, new("sparse-metric", "decrease", "10%", 3), CostGoalOutcomeState.InsufficientData, null, 10, 0, 3, null));

        var output = await RenderDetailAsync(goal.Id);

        Assert.Equal(new[]
        {
            "Cost-goal outcomes:",
            "  line=1 metric=pending-metric state=pending before=10.5 after=unavailable target=10% direction=decrease beforeSamples=5 afterSamples=2 progress=2/3",
            "  line=2 metric=met-metric state=met before=20 after=30 target=10% direction=increase beforeSamples=3 afterSamples=3",
            "  line=3 metric=missed-metric state=missed before=30 after=29 target=10% direction=decrease beforeSamples=4 afterSamples=3",
            "  line=4 metric=sparse-metric state=insufficient-data before=unavailable after=10 target=10% direction=decrease beforeSamples=0 afterSamples=3"
        }, output.Text.Split(Environment.NewLine).SkipWhile(line => line != "Cost-goal outcomes:")
            .TakeWhile(line => !line.StartsWith("Recent events:", StringComparison.Ordinal)).ToArray());
        AssertOutcomeFields(output, "0", ["pending", "1", "pending-metric", "10.5", null, "10%", "decrease", "5", "2", "2", "3"]);
        AssertOutcomeFields(output, "1", ["met", "2", "met-metric", "20", "30", "10%", "increase", "3", "3"]);
        AssertOutcomeFields(output, "2", ["missed", "3", "missed-metric", "30", "29", "10%", "decrease", "4", "3"]);
        AssertOutcomeFields(output, "3", ["insufficient-data", "4", "sparse-metric", null, "10", "10%", "decrease", "0", "3"]);
    }

    [Fact]
    public async Task DetailWithoutRecordRendersNoOutcomeSection()
    {
        var goal = _harness.AddGoal(GoalIdWithRecord, "Cost goal", AgentRole.Developer);

        var output = await RenderDetailAsync(goal.Id);

        Assert.DoesNotContain("Cost-goal outcomes:", output.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(output.Structured!.Detail!.Fields, field => field.Key.StartsWith("outcome/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BoardRowShowsPendingSummary()
    {
        var goal = _harness.AddGoal(GoalIdWithRecord, "Cost goal", AgentRole.Developer);
        WriteRecord(goal.Id, new CostGoalOutcomeResult(0, new("metric", "decrease", "10%", 3),
            CostGoalOutcomeState.Pending, 10, null, 5, 2, null));

        var row = await ReadBoardRowAsync();

        Assert.Equal("11111111 | Cost goal | Active | Developer | 2m | outcomes pending=1 met=0 missed=0 insufficient-data=0 progress=line1:2/3", row);
    }

    [Fact]
    public async Task BoardRowWithoutSummaryKeepsLegacyText()
    {
        _harness.AddGoal(GoalIdWithRecord, "Cost goal", AgentRole.Developer);

        var row = await ReadBoardRowAsync();

        Assert.Equal("11111111 | Cost goal | Active | Developer | 2m", row);
    }

    private async Task<OwnerConsoleCapturedOutput> RenderDetailAsync(GoalId id)
    {
        var query = new OwnerGoalDetailQuery(_harness.State, _harness.Tail, orchestratorDirectory: _directory);
        var snapshot = await query.LoadAsync(id.Value, CancellationToken.None);
        Assert.NotNull(snapshot);
        var output = new OwnerConsoleCapturedOutput();
        OwnerConsoleGoalDetailFormatter.Render(snapshot, output, TimeZoneInfo.Utc);
        return output;
    }

    private static void AssertOutcomeFields(OwnerConsoleCapturedOutput output, string index, string?[] expected)
    {
        string[] keys = ["state", "line", "metric", "before", "after", "target", "direction",
            "before-samples", "after-samples", "k", "n"];
        var prefix = "outcome/" + index + "/";
        var fields = output.Structured!.Detail!.Fields.Where(field => field.Key.StartsWith(prefix, StringComparison.Ordinal));
        Assert.Equal(keys.Take(expected.Length).Select(key => prefix + key).Zip(expected),
            fields.Select(field => (field.Key, field.Value)));
    }

    private async Task<string> ReadBoardRowAsync()
    {
        var snapshot = _harness.Kernel.ExportSnapshot();
        _harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "fixture", _harness.Clock.GetUtcNow().AddMinutes(-2))]
            }).ToArray()
        });
        using var session = new OwnerConsoleSession(_harness.State, _harness.Questions, _harness.Answers,
            _harness.Liveness, _harness.Digest, _harness.Tail, _harness.Output, _harness.Clock,
            orchestratorDirectory: _directory);
        await session.HandleCommandAsync("board", CancellationToken.None);
        return Assert.Single(_harness.Output.Text.Split(Environment.NewLine),
            line => line.StartsWith("11111111 | ", StringComparison.Ordinal));
    }

    private void WriteRecord(GoalId id, params CostGoalOutcomeResult[] outcomes)
    {
        var filing = new CostGoalOutcomeFiling(_harness.Clock.GetUtcNow(), null, outcomes.Select(row =>
            new OutcomeMetricBaseline(row.Line, row.Before, row.BeforeSampleSize, null)).ToArray());
        var final = outcomes.Where(row => row.State != CostGoalOutcomeState.Pending).ToArray();
        var pending = outcomes.Where(row => row.State == CostGoalOutcomeState.Pending).ToArray();
        new CostGoalOutcomeStore(_directory).Write(id, new(filing, final, [], null, false) { PendingResults = pending });
    }

    public void Dispose() => SharedTestSupport.RemoveTempDirectory(_directory);
}
