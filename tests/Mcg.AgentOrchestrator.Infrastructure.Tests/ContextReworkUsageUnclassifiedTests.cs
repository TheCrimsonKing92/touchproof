using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case owns its in-memory goal and report writers.
public sealed class ContextReworkUsageUnclassifiedTests
{
    internal static readonly DateTimeOffset RetryAt = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(RetryCause.NewSourceFinding, null, FindingCategory.Correctness)]
    [InlineData(RetryCause.NewSourceFinding, "missing", FindingCategory.Correctness)]
    [InlineData(RetryCause.NewTestFinding, "recorded", FindingCategory.Unspecified)]
    [InlineData(RetryCause.ProviderInterruption, "recorded", FindingCategory.Correctness)]
    [InlineData(null, null, FindingCategory.Correctness)]
    public void ReportsRetryUsageUnderUnclassified(
        RetryCause? cause, string? linkedFinding, FindingCategory recordedCategory)
    {
        var retry = CreateRetriedTask("dev", 120, cause, linkedFinding is null ? [] : [linkedFinding]);
        var review = new TaskSnapshot("review", "Review", AgentRole.Reviewer, WorkTaskStatus.Completed,
            null, null, null,
            [new("review", "fixture", 0, "", "", RetryAt.AddMinutes(-1), MergedReviewFindings:
                [new("recorded", ReviewFindingState.Open, new("src/Foo.cs", "Method"), "Fixture finding",
                    Category: recordedCategory)])],
            null, null);
        var goal = new GoalSnapshot("ab123456ab123456ab123456ab123456", "Unclassified retry", GoalStatus.Active,
            [retry, review], []);
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([goal], []));
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = CliContextUsageCommand.Run(["context-usage", "--rework", "--json"], () => kernel.Goals, output, error);

        Assert.True(exit == 0, error.ToString());
        using var report = JsonDocument.Parse(output.ToString());
        var row = Assert.Single(report.RootElement.GetProperty("rows").EnumerateArray());
        Assert.Equal("Developer", row.GetProperty("role").GetString());
        Assert.Equal((cause ?? RetryCause.Unknown).ToString(), row.GetProperty("retryCause").GetString());
        Assert.Equal("unclassified", row.GetProperty("findingClass").GetString());
        Assert.Equal(1, row.GetProperty("attempts").GetInt32());
        Assert.Equal(120L, row.GetProperty("input").GetInt64());
        Assert.Equal(30L, row.GetProperty("cached").GetInt64());
        Assert.Equal(12L, row.GetProperty("output").GetInt64());
        var example = Assert.Single(row.GetProperty("examples").EnumerateArray());
        Assert.Equal(goal.Id, example.GetProperty("goalId").GetString());
        Assert.Equal(retry.Id, example.GetProperty("taskId").GetString());
    }

    internal static TaskSnapshot CreateRetriedTask(
        string id, long input, RetryCause? cause, IReadOnlyList<string> linkedFindings)
    {
        var first = new TaskDispatchSnapshot("worker", "first dispatch", "fixture", RetryAt.AddMinutes(-2),
            ContextPackageReceipt: ContextUsageTestFixture.Receipt(1000, 250, 100, 0));
        var retry = new TaskDispatchSnapshot("worker", "retry dispatch", "fixture", RetryAt,
            ContextPackageReceipt: ContextUsageTestFixture.Receipt(input, input / 4, input / 10, 0));
        return new TaskSnapshot(id, "Implementation", AgentRole.Developer, WorkTaskStatus.Completed,
            null, null, null, [], retry, null, DispatchHistory: [first, retry],
            RetryAdmissionHistory: cause is { } recordedCause
                ? [new("retry-admission", recordedCause, new(1, "fixture"), RetryAdmissionDecision.Allowed,
                    RetryAdmissionRoute.SameRole, PaidRouteClassification.Unknown, RetryAt, RetryAt.AddSeconds(-1),
                    StableFindingIds: linkedFindings)]
                : []);
    }
}
