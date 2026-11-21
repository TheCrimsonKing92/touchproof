using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ContextUsageArchiveReadOnlyTests
{
    [Fact]
    public async Task UsageReads_ArchivedGoal_PreservePersistedRowsAndSchemaVersion()
    {
        // Parallel-safe: this test owns its state database.
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var workspace = OrchestratorWorkspace.ForDirectory(root, root, null,
                new OrchestratorProjectRegistry(Path.Combine(root, "registry"), root));
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            var repository = InfrastructureTestSupport.CreateMigratedStateRepository(workspace.SqliteStatePath);
            var dispatchedAt = new DateTimeOffset(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);
            var goalId = GoalId.New().Value;
            var dispatch = new TaskDispatchSnapshot("fixture", "fixture", "fixture", dispatchedAt,
                ProviderUsage: new DispatchProviderUsage(
                    ProviderUsageValue.Reported(24), ProviderUsageValue.Reported(0), ProviderUsageValue.Reported(0)));
            var task = new TaskSnapshot(TaskId.New().Value, "Completed implementation", AgentRole.Developer,
                WorkTaskStatus.Completed, null, null, null, null, dispatch, null,
                DispatchHistory: [dispatch], WorkerBuildInvocations: []);
            await repository.SaveGoalSnapshotsAsync([
                new GoalSnapshot(goalId, "Archived worker usage", GoalStatus.Completed, [task],
                    [new ProgressEventSnapshot(goalId, null, ProgressKind.GoalPolicyDecision, "Completed", dispatchedAt)])
            ]);
            Assert.Equal(1, await repository.ArchiveTerminalGoalSnapshotsAsync(dispatchedAt.AddDays(1), compressSnapshots: true));
            Assert.True(Assert.Single((await repository.LoadAsync()).Goals).IsMetadataOnly);

            using var connection = StateDbConnectionFactory.Open(workspace.SqliteStatePath, StateDbConnectionProfile.QueryOnlyRead);
            var before = ReadState(connection);
            Assert.Single(before.Goals);
            Assert.Single(before.Archive);
            Assert.Single(before.History);

            string[] arguments = ["context-usage", "--since", dispatchedAt.ToString("O", CultureInfo.InvariantCulture)];
            Assert.Contains("Coverage: live=0 archived=1 unknown-usage=0 conflicting=0", RunReport(workspace, arguments));
            AssertStateUnchanged(before, ReadState(connection));

            using var report = JsonDocument.Parse(RunReport(workspace, [.. arguments, "--json"]));
            Assert.Equal(1, report.RootElement.GetProperty("coverage").GetProperty("archived").GetInt32());
            AssertStateUnchanged(before, ReadState(connection));

            var metric = new OutcomeMetricLine(CostGoalOutcomeMetric.WorkerInput, "decrease", "10%", 1, Role: AgentRole.Developer);
            var samples = WorkerUsageSampleSource.Read(workspace.SqliteStatePath).Measure(metric, dispatchedAt, dispatchedAt);
            Assert.Equal(new OutcomeMetricSample(24, 1), samples.After);
            AssertStateUnchanged(before, ReadState(connection));
            Assert.True(Assert.Single((await repository.LoadAsync()).Goals).IsMetadataOnly);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private static string RunReport(OrchestratorWorkspace workspace, string[] arguments)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.True(CliContextUsageCommand.Run(arguments, workspace, output, error) == 0, error.ToString());
        Assert.Equal(string.Empty, error.ToString());
        return output.ToString();
    }

    private static PersistedState ReadState(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return new(
            (long)command.ExecuteScalar()!,
            ReadRows(connection, "SELECT * FROM goals ORDER BY id"),
            ReadRows(connection, "SELECT * FROM goal_snapshot_archive ORDER BY goal_id"),
            ReadRows(connection, "SELECT * FROM goal_task_history ORDER BY goal_id, task_id, kind, sequence"));
    }

    private static List<string> ReadRows(SqliteConnection connection, string query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = query;
        using var reader = command.ExecuteReader();
        var rows = new List<string>();
        while (reader.Read())
        {
            var values = new object?[reader.FieldCount];
            for (var index = 0; index < values.Length; index++)
            {
                values[index] = reader.GetValue(index) switch
                {
                    DBNull => null,
                    byte[] blob => new { Blob = blob },
                    var value => value
                };
            }
            rows.Add(JsonSerializer.Serialize(values));
        }
        return rows;
    }

    private static void AssertStateUnchanged(PersistedState before, PersistedState after)
    {
        Assert.Equal(before.SchemaVersion, after.SchemaVersion);
        Assert.Equal(before.Goals, after.Goals);
        Assert.Equal(before.Archive, after.Archive);
        Assert.Equal(before.History, after.History);
    }

    private sealed record PersistedState(long SchemaVersion, List<string> Goals, List<string> Archive, List<string> History);
}
