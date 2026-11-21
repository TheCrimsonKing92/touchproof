using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Usage;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.Infrastructure.Tests;

public sealed class ContextUsageArchiveCoverageTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 14, 6, 0, 0, TimeSpan.Zero);
    private const string Since = "2026-11-14T00:00:00-06:00";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TotalsAreIdenticalBeforeAndAfterArchival(bool compress)
    {
        using var fixture = new Fixture();
        var earlier = NewDispatch(Start, 25) with { ContextPackageReceipt = Receipt(20) };
        var latest = NewDispatch(Start.AddHours(1), 40);
        var goal = NewGoal(GoalStatus.Completed, latest, [NewDispatch(Start.AddMinutes(-1), 800), earlier, latest]);
        await fixture.Repository.SaveGoalSnapshotsAsync([goal]);

        using var before = fixture.ReadJson();
        var beforeRows = before.RootElement.GetProperty("rows");
        var row = Assert.Single(beforeRows.EnumerateArray());
        Assert.Equal(2, row.GetProperty("dispatchCount").GetInt32());
        Assert.Equal(30, row.GetProperty("input").GetProperty("median").GetDouble());
        Assert.Equal(9, row.GetProperty("turns").GetProperty("p90").GetInt64());
        Assert.Equal(6, row.GetProperty("toolCalls").GetProperty("p90").GetInt64());
        Assert.Equal(18, row.GetProperty("harnessOverhead").GetProperty("p90").GetInt64());
        AssertCoverage(before.RootElement, live: 2, archived: 0);

        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Start.AddDays(2), compressSnapshots: compress));
        Assert.True(Assert.Single((await fixture.Repository.LoadAsync()).Goals).IsMetadataOnly);

        using var after = fixture.ReadJson();
        Assert.Equal(beforeRows.GetRawText(), after.RootElement.GetProperty("rows").GetRawText());
        AssertCoverage(after.RootElement, live: 0, archived: 2);
    }

    [Fact]
    public async Task LiveAndArchivedGoalsReportOnceWithCoverageCounts()
    {
        using var fixture = new Fixture();
        var live = NewGoal(GoalStatus.Active, NewDispatch(Start, null));
        var archiveOnly = NewGoal(GoalStatus.Completed, NewDispatch(Start, 0));
        var overlap = NewGoal(GoalStatus.Active, NewDispatch(Start, 900));
        await fixture.Repository.SaveGoalSnapshotsAsync([live, archiveOnly, overlap]);
        Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(Start.AddDays(2)));

        var task = Assert.Single(overlap.Tasks);
        fixture.InsertArchive(overlap with
        {
            Tasks = [task with { LastDispatch = task.LastDispatch! with { ContextPackageReceipt = Receipt(40) } }]
        });

        var output = fixture.ReadText();
        Assert.Contains("Context usage: dispatches=3 rows=1", output);
        Assert.Contains("input=median=20,p90=40", output);
        Assert.Contains("Coverage: live=2 archived=1 unknown-usage=1 conflicting=0", output);
        using var report = fixture.ReadJson();
        AssertCoverage(report.RootElement, live: 2, archived: 1, unknown: 1);
    }

    [Fact]
    public void UnresolvedConflictIsExcludedAndListed()
    {
        var archivedAt = Start.AddDays(2);
        var goalId = GoalId.New().Value;
        var taskId = TaskId.New().Value;
        ArchivedDispatchUsage Copy(int sequence, long input, DateTimeOffset time) =>
            new(goalId, taskId, sequence, AgentRole.Developer,
                new("", "", "", Start, ModelName: "fixture", ProviderUsage: ProviderUsage(input)), time);
        var archived = new[]
        {
            Copy(0, 1000, archivedAt),
            Copy(0, 2000, archivedAt),
            Copy(1, 3000, archivedAt),
            Copy(1, 20, archivedAt.AddMinutes(1)),
            Copy(1, 4000, archivedAt)
        };
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        var exitCode = CliContextUsageCommand.Run(["context-usage"], () => [], output, error,
            (since, role) => DispatchUsagePopulation.Build([], since, role, archived));

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Context usage: dispatches=1 rows=1", output.ToString());
        Assert.Contains("input=median=20,p90=20", output.ToString());
        Assert.Contains("Coverage: live=0 archived=1 unknown-usage=0 conflicting=1", output.ToString());
        Assert.Contains($"Conflicting dispatch: goal={goalId} task={taskId} sequence=0", output.ToString());
    }

    private static void AssertCoverage(JsonElement report, int live, int archived, int unknown = 0)
    {
        var coverage = report.GetProperty("coverage");
        Assert.Equal(live, coverage.GetProperty("live").GetInt32());
        Assert.Equal(archived, coverage.GetProperty("archived").GetInt32());
        Assert.Equal(unknown, coverage.GetProperty("unknownUsage").GetInt32());
        Assert.Equal(0, coverage.GetProperty("conflicting").GetInt32());
    }

    private static GoalSnapshot NewGoal(GoalStatus status, TaskDispatchSnapshot latest,
        IReadOnlyList<TaskDispatchSnapshot>? history = null)
    {
        var id = GoalId.New().Value;
        var taskStatus = status == GoalStatus.Completed ? WorkTaskStatus.Completed : WorkTaskStatus.Assigned;
        var task = new TaskSnapshot(TaskId.New().Value, "Fixture task", AgentRole.Developer, taskStatus,
            null, null, null, null, latest, null, DispatchHistory: history, WorkerBuildInvocations: []);
        return new(id, "Fixture goal", status, [task],
            [new ProgressEventSnapshot(id, null, ProgressKind.GoalCreated, "Created", Start.AddDays(-1)),
            new ProgressEventSnapshot(id, null, ProgressKind.GoalPolicyDecision, "Updated", Start.AddDays(1))]);
    }

    private static TaskDispatchSnapshot NewDispatch(DateTimeOffset at, long? input) =>
        new("fixture", "fixture", "fixture", at, ModelName: "fixture",
            ProviderUsage: input is { } value ? ProviderUsage(value) : null);

    private static DispatchProviderUsage ProviderUsage(long input) =>
        new(ProviderUsageValue.Reported(input), ProviderUsageValue.Reported(0), ProviderUsageValue.Reported(3))
        {
            Turns = ProviderUsageValue.Reported(1),
            ToolCalls = ProviderUsageValue.Reported(2)
        };

    private static WorkerContextPackageReceipt Receipt(long input) =>
        new("fixture", [], ProviderUsageValue.Reported(input), ProviderUsageValue.Reported(0), ProviderUsageValue.Reported(3),
            ModelInputTokenEstimate: 2, Turns: ProviderUsageValue.Reported(9))
        {
            ToolCalls = ProviderUsageValue.Reported(6)
        };

    private sealed class Fixture : IDisposable
    {
        private readonly DirectoryInfo root = Directory.CreateTempSubdirectory("context-usage-archive-");
        private readonly OrchestratorWorkspace workspace;
        internal SqliteOrchestratorStateRepository Repository { get; }

        internal Fixture()
        {
            workspace = OrchestratorWorkspace.ForDirectory(root.FullName, root.FullName, null,
                new OrchestratorProjectRegistry(Path.Combine(root.FullName, "registry"), root.FullName));
            Directory.CreateDirectory(workspace.OrchestratorDirectory);
            StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
            Repository = new(workspace.SqliteStatePath);
        }

        internal JsonDocument ReadJson() => JsonDocument.Parse(Read("--json"));
        internal string ReadText() => Read();

        internal void InsertArchive(GoalSnapshot snapshot)
        {
            using var connection = StateDbConnectionFactory.Open(workspace.SqliteStatePath, StateDbConnectionProfile.ReadWrite);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO goal_snapshot_archive (goal_id, snapshot_json, archived_at)
                VALUES ($id, $snapshot, $at)
                """;
            command.Parameters.AddWithValue("$id", snapshot.Id);
            command.Parameters.AddWithValue("$snapshot",
                JsonSerializer.Serialize(snapshot, SqliteOrchestratorStateRepository.HistorySerializerOptions));
            command.Parameters.AddWithValue("$at", Start.AddDays(2).ToString("O", CultureInfo.InvariantCulture));
            Assert.Equal(1, command.ExecuteNonQuery());
        }

        private string Read(params string[] extraArgs)
        {
            using var output = new StringWriter(CultureInfo.InvariantCulture);
            using var error = new StringWriter(CultureInfo.InvariantCulture);
            var exitCode = CliContextUsageCommand.Run(
                ["context-usage", "--since", Since, "--role", "developer", .. extraArgs], workspace, output, error);
            Assert.True(exitCode == 0, error.ToString());
            Assert.Equal(string.Empty, error.ToString());
            return output.ToString();
        }

        public void Dispose() => root.Delete(recursive: true);
    }
}
