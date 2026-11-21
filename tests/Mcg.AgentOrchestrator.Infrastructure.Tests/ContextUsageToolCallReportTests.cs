using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: uses an in-memory goal and local writers.
public sealed class ContextUsageToolCallReportTests
{
    [Fact]
    public void ReportUsesKnownToolCallsAndFallsBackFromUnknownReceipts()
    {
        var rows = ContextUsageReport.Build([CreateGoal()]).Rows;

        Assert.Collection(rows,
            mixed =>
            {
                Assert.Equal("Developer", mixed.Role);
                Assert.Equal("counted-model", mixed.Model);
                Assert.Equal(4, mixed.DispatchCount);
                Assert.Equal(new ContextUsageStat(4, 9), mixed.ToolCalls);
            },
            unknown =>
            {
                Assert.Equal("unknown-model", unknown.Model);
                Assert.Equal(new ContextUsageStat(null, null), unknown.ToolCalls);
            },
            zero =>
            {
                Assert.Equal("Tester", zero.Role);
                Assert.Equal("counted-model", zero.Model);
                Assert.Equal(new ContextUsageStat(0, 0), zero.ToolCalls);
            });
    }

    [Fact]
    public void CliRendersToolCallStatisticsBesideTurnsInTextAndJson()
    {
        var goal = CreateGoal();

        var text = Run(goal);
        Assert.Contains("turns=median=unknown,p90=unknown tool-calls=median=4,p90=9", text);
        Assert.Contains("tool-calls=median=unknown,p90=unknown", text);
        Assert.Contains("tool-calls=median=0,p90=0", text);

        using var json = JsonDocument.Parse(Run(goal, "--json"));
        Assert.Collection(json.RootElement.GetProperty("rows").EnumerateArray(),
            mixed => AssertToolCalls(mixed, 4, 9),
            unknown => AssertToolCalls(unknown, null, null),
            zero => AssertToolCalls(zero, 0, 0));
    }

    private static void AssertToolCalls(JsonElement row, double? median, long? p90)
    {
        var toolCalls = row.GetProperty("toolCalls");
        Assert.Equal(median, toolCalls.GetProperty("median").Deserialize<double?>());
        Assert.Equal(p90, toolCalls.GetProperty("p90").Deserialize<long?>());
    }

    private static string Run(Goal goal, params string[] args)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliContextUsageCommand.Run(["context-usage", .. args], () => [goal], output, error));
        Assert.Empty(error.ToString());
        return output.ToString();
    }

    private static Goal CreateGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer);
        var tester = new TaskSpec(TaskId.New(), "Test", AgentRole.Tester);
        var goal = kernel.CreateGoal("Measure worker tool calls", [developer, tester]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var receipt = ContextUsageTestFixture.Receipt(80, 50, 20, 0);

        Add(developer, 0, "counted-model", receipt: receipt with { ToolCalls = ProviderUsageValue.Reported(2) });
        Add(developer, 1, "counted-model", usage: Usage(4));
        Add(developer, 2, "counted-model", receipt: receipt, usage: Usage(9));
        Add(developer, 3, "counted-model", receipt: receipt);
        Add(developer, 4, "unknown-model", usage: Usage(null));
        Add(tester, 5, "counted-model", receipt: receipt with { ToolCalls = ProviderUsageValue.Reported(0) },
            usage: Usage(8));
        return goal;

        void Add(TaskSpec task, int minute, string model, WorkerContextPackageReceipt? receipt = null,
            DispatchProviderUsage? usage = null) =>
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("test-worker", "test command", "work", ContextUsageTestFixture.Start.AddMinutes(minute),
                    ModelName: model, ContextPackageReceipt: receipt, ProviderUsage: usage),
                allowPendingRecordedDispatchRefresh: true);

        static DispatchProviderUsage Usage(long? count) =>
            DispatchProviderUsage.From(new ProviderReportedUsage(80, 20, 50, ToolCallCount: count), null);
    }
}
