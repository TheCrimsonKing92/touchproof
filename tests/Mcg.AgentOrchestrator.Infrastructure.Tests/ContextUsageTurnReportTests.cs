using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: uses an in-memory goal and local writers.
public sealed class ContextUsageTurnReportTests
{
    [Fact]
    public void Cli_ReportsTurnStatisticsPerRoleAndModelAndKeepsMissingUsageCounts()
    {
        var goal = CreateGoal();
        var text = Run(goal);
        Assert.Contains("Developer | claude-test | dispatches=4 reported-input=3", text);
        Assert.Contains("turns=median=4.5,p90=7", text);
        Assert.Contains("missing-usage=1 reasons=unreadable=1", text);

        using var json = JsonDocument.Parse(Run(goal, "--json"));
        var rows = json.RootElement.GetProperty("rows").EnumerateArray();
        Assert.Collection(rows,
            developerClaude =>
            {
                AssertRow(developerClaude, "Developer", "claude-test", 4.5, 7);
                Assert.Equal(4, developerClaude.GetProperty("dispatchCount").GetInt32());
                Assert.Equal(3, developerClaude.GetProperty("reportedInputCount").GetInt32());
                Assert.Equal(1, developerClaude.GetProperty("missingUsageCount").GetInt32());
                Assert.Equal(1, developerClaude.GetProperty("missingUsageReasons").GetProperty("unreadable").GetInt32());
            },
            developerCodex =>
            {
                AssertRow(developerCodex, "Developer", "codex-test", null, null);
                Assert.Equal(1, developerCodex.GetProperty("dispatchCount").GetInt32());
                Assert.Equal(0, developerCodex.GetProperty("missingUsageCount").GetInt32());
            },
            testerCodex => AssertRow(testerCodex, "Tester", "codex-test", 12, 12));
    }

    [Fact]
    public void Cli_RoleFilterLimitsTurnRowsInTextAndJson()
    {
        var goal = CreateGoal();
        var text = Run(goal, "--role", "tester");
        Assert.Contains("Context usage: dispatches=1 rows=1", text);
        Assert.Contains("Tester | codex-test | dispatches=1", text);
        Assert.Contains("turns=median=12,p90=12", text);
        Assert.DoesNotContain("Developer |", text);

        using var json = JsonDocument.Parse(Run(goal, "--role", "tester", "--json"));
        var row = Assert.Single(json.RootElement.GetProperty("rows").EnumerateArray());
        AssertRow(row, "Tester", "codex-test", 12, 12);
    }

    private static void AssertRow(JsonElement row, string role, string model, double? median, long? p90)
    {
        Assert.Equal(role, row.GetProperty("role").GetString());
        Assert.Equal(model, row.GetProperty("model").GetString());
        var turns = row.GetProperty("turns");
        Assert.Equal(median, turns.GetProperty("median").Deserialize<double?>());
        Assert.Equal(p90, turns.GetProperty("p90").Deserialize<long?>());
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
        var goal = kernel.CreateGoal("Measure worker turns", [
            new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer),
            new TaskSpec(TaskId.New(), "Test", AgentRole.Tester)]);
        kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var developer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Developer);
        var tester = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Tester);
        var at = ContextUsageTestFixture.Start;

        var receipt = ContextUsageTestFixture.Receipt(80, 50, 20, 0) with
        {
            Turns = ProviderUsageValue.Reported(2)
        };
        ContextUsageTestFixture.Add(kernel, goal, developer, at, "claude-test", receipt);
        AddUsage(developer, 1, "claude-test", DispatchProviderUsage.From(new(80, 20, 50, 7), null));
        AddUsage(developer, 2, "claude-test", DispatchProviderUsage.From(new(80, 20, 50), null));
        AddUsage(developer, 3, "claude-test", DispatchProviderUsage.From(null, "unreadable"));
        AddUsage(developer, 4, "codex-test", new DispatchProviderUsage(
            receipt.InputTokens, receipt.CachedInputTokens, receipt.OutputTokens));
        AddUsage(tester, 5, "codex-test", DispatchProviderUsage.From(new(80, 20, 50, 12), null));
        return goal;

        void AddUsage(TaskSpec task, int minute, string model, DispatchProviderUsage usage) =>
            kernel.RecordTaskDispatch(goal.Id, task.Id,
                new TaskDispatchRecord("test-worker", "test command", "C:\\work", at.AddMinutes(minute),
                    ModelName: model, ProviderUsage: usage),
                allowPendingRecordedDispatchRefresh: true);
    }
}
