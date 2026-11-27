using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Usage;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ContextUsageWindowTests
{
    private const string Since = "2026-09-15T07:01:00-05:00";
    private const string Until = "2026-09-15T12:03:00+00:00";

    [Fact]
    public void PlainJsonReportsHalfOpenWindowAndBothBounds()
    {
        var goal = ContextUsageTestFixture.CreateGoal();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = CliContextUsageCommand.Run(
            ["context-usage", "--since", Since, "--until", Until, "--role", "developer", "--json"],
            () => [goal], output, error);

        Assert.True(exit == 0, error.ToString());
        Assert.Equal(string.Empty, error.ToString());
        using var document = JsonDocument.Parse(output.ToString());
        var report = document.RootElement;
        Assert.Equal(Since, report.GetProperty("since").GetString());
        Assert.Equal(Until, report.GetProperty("until").GetString());
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal(2, row.GetProperty("dispatchCount").GetInt32());
        Assert.Equal(2, row.GetProperty("reportedInputCount").GetInt32());
        var input = row.GetProperty("input");
        // The fixture records 20,000 at the start and 30,000 strictly inside the window.
        Assert.Equal(25000, input.GetProperty("median").GetDouble());
        Assert.Equal(30000, input.GetProperty("p90").GetInt64());
        Assert.Equal(2, report.GetProperty("coverage").GetProperty("live").GetInt32());
    }

    [Theory]
    [InlineData(Since, "2026-09-15T07:01:00.0000000-05:00", 2)]
    [InlineData(null, "unbounded", 3)]
    public void PlainTextPrintsTheRequestedWindow(string? since, string startLabel, int expectedDispatches)
    {
        var goal = ContextUsageTestFixture.CreateGoal();
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        string[] startArgs = since is null ? [] : ["--since", since];
        var exit = CliContextUsageCommand.Run(
            ["context-usage", .. startArgs, "--until", Until], () => [goal], output, error);

        Assert.True(exit == 0, error.ToString());
        Assert.Equal(string.Empty, error.ToString());
        Assert.StartsWith(
            $"Context usage [{startLabel}, 2026-09-15T12:03:00.0000000+00:00): dispatches={expectedDispatches} rows=1{Environment.NewLine}",
            output.ToString());
    }

    [Fact]
    public void InMemoryArchiveBuildExcludesDispatchesAfterUntil()
    {
        var start = ContextUsageTestFixture.Start;
        var goalId = GoalId.New().Value;
        var taskId = TaskId.New().Value;
        var archivedAt = start.AddDays(1);
        var inside = new TaskDispatchRecord("fixture", "fixture", "fixture", start.AddMinutes(2));
        var after = inside with { DispatchedAt = start.AddMinutes(4) };
        ArchivedDispatchUsage[] archived =
        [
            new(goalId, taskId, 0, AgentRole.Developer, inside, archivedAt),
            new(goalId, taskId, 1, AgentRole.Developer, after, archivedAt)
        ];

        var population = DispatchUsagePopulation.Build([], start.AddMinutes(1), archived: archived,
            until: start.AddMinutes(3));

        Assert.Equal(1, population.Archived);
        Assert.Equal(inside.DispatchedAt, Assert.Single(population.Dispatches).Dispatch.DispatchedAt);
    }

    [Fact]
    public async Task WorkspaceReportExcludesArchivedDispatchesAfterUntilFromRowsAndCoverage()
    {
        // Parallel-safe. The test owns its state database.
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root, root, null,
                new OrchestratorProjectRegistry(Path.Combine(root, "registry"), root));
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
            var start = ContextUsageTestFixture.Start;
            var inside = ArchiveDispatch(start.AddMinutes(2), 30);
            var after = ArchiveDispatch(start.AddMinutes(4), 800);
            var goalId = GoalId.New().Value;
            var task = new TaskSnapshot(TaskId.New().Value, "Completed implementation", AgentRole.Developer,
                WorkTaskStatus.Completed, null, null, null, null, after, null,
                DispatchHistory: [inside, after], WorkerBuildInvocations: []);
            await repository.SaveGoalSnapshotsAsync([
                new GoalSnapshot(goalId, "Archived worker usage", GoalStatus.Completed, [task],
                    [new ProgressEventSnapshot(goalId, null, ProgressKind.GoalPolicyDecision, "Completed", after.DispatchedAt)])
            ]);
            Assert.Equal(1, await repository.ArchiveTerminalGoalSnapshotsAsync(start.AddDays(1)));
            Assert.True(Assert.Single((await repository.LoadAsync()).Goals).IsMetadataOnly);

            using var bounded = ReadWorkspaceReport(workspace, "--until", Until);
            AssertArchivedReport(bounded.RootElement, expectedDispatches: 1, expectedP90: 30);
            using var unbounded = ReadWorkspaceReport(workspace);
            AssertArchivedReport(unbounded.RootElement, expectedDispatches: 2, expectedP90: 800);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static TaskDispatchSnapshot ArchiveDispatch(DateTimeOffset at, long input) =>
        new("fixture", "fixture", "fixture", at, ModelName: "fixture",
            ProviderUsage: new DispatchProviderUsage(
                ProviderUsageValue.Reported(input), ProviderUsageValue.Reported(0), ProviderUsageValue.Reported(0)));

    private static JsonDocument ReadWorkspaceReport(OrchestratorWorkspace workspace, params string[] extraArgs)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var exit = CliContextUsageCommand.Run(
            ["context-usage", "--since", Since, "--json", .. extraArgs], workspace, output, error);
        Assert.True(exit == 0, error.ToString());
        Assert.Equal(string.Empty, error.ToString());
        return JsonDocument.Parse(output.ToString());
    }

    private static void AssertArchivedReport(JsonElement report, int expectedDispatches, long expectedP90)
    {
        var row = Assert.Single(report.GetProperty("rows").EnumerateArray());
        Assert.Equal(expectedDispatches, row.GetProperty("dispatchCount").GetInt32());
        Assert.Equal(expectedP90, row.GetProperty("input").GetProperty("p90").GetInt64());
        var coverage = report.GetProperty("coverage");
        Assert.Equal(0, coverage.GetProperty("live").GetInt32());
        Assert.Equal(expectedDispatches, coverage.GetProperty("archived").GetInt32());
    }
}
