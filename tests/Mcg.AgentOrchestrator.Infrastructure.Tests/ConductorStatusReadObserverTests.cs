using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: private workspace/database, local observers/writers and fixed time/probe.
public sealed class ConductorStatusReadObserverTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-10-17T11:07:00Z");
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    private const string Active = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Failed = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Completed = "cccccccccccccccccccccccccccccccc";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Status_ObserverCoversEveryStateReadAndPreservesOutput(bool warm)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        await Seed(fixture.Workspace);
        if (warm)
            await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath)
                .ReadProviderHoldSeedEvidenceAsync([new(Failed), new(Completed)]);
        var observations = new List<GoalSnapshotReadObservation>();
        var withObserver = Status(fixture.Workspace, observations.Add);
        Assert.Equal(Status(fixture.Workspace, null), withObserver);
        Assert.Contains("Provider usage holds: 1", withObserver);
        Assert.Equal(Active, Assert.Single(observations.Where(row => row.Operation == "requested")).GoalId);
        Assert.True(Assert.Single(observations.Where(row => row.Operation == "metadata")).MaterializedCharacters > 0);
        Assert.True(Assert.Single(observations.Where(row => row.Operation == "model-fit-history")).MaterializedCharacters > 0);
        Assert.Single(observations.Where(row => row.Operation == "latest-landing"));
        Assert.Single(observations.Where(row => row.Operation == "human-input"));
        var evidence = observations.Where(row => row.Operation == (warm
            ? "provider-evidence-stored" : "provider-evidence-decoded")).ToArray();
        Assert.Equal(new[] { Failed, Completed }, evidence.Select(row => row.GoalId).Order(StringComparer.Ordinal));
        Assert.All(evidence, row => Assert.True(row.MaterializedCharacters > 0));
        using var connection = Open(fixture.Workspace);
        using var command = connection.CreateCommand();
        command.CommandText = warm
            ? "SELECT evidence_json FROM provider_hold_seed_evidence ORDER BY goal_id"
            : "SELECT snapshot_json FROM goals WHERE id <> 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa' ORDER BY id";
        using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var expectedCharacters = new List<long>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) expectedCharacters.Add(reader.GetString(0).Length);
        Assert.Equal(expectedCharacters, evidence.OrderBy(row => row.GoalId, StringComparer.Ordinal)
            .Select(row => row.MaterializedCharacters));
        Assert.DoesNotContain(observations, row => row.Operation == (warm
            ? "provider-evidence-decoded" : "provider-evidence-stored"));
    }

    [Fact]
    public async Task Status_InvalidStoredEvidenceAccountsForFallbackDecode()
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        await Seed(fixture.Workspace);
        await new SqliteOrchestratorStateRepository(fixture.Workspace.SqliteStatePath)
            .ReadProviderHoldSeedEvidenceAsync([new(Failed), new(Completed)]);
        using var connection = Open(fixture.Workspace);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE provider_hold_seed_evidence SET evidence_json = '{}' WHERE goal_id = $id";
        command.Parameters.AddWithValue("$id", Completed);
        Assert.Equal(1, await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken));
        var observations = new List<GoalSnapshotReadObservation>();
        Assert.Contains("Provider usage holds: 1", Status(fixture.Workspace, observations.Add));
        Assert.Equal(2, Assert.Single(observations.Where(row => row.Operation == "provider-evidence-stored" &&
            row.GoalId == Completed)).MaterializedCharacters);
        Assert.Equal(Completed, Assert.Single(observations.Where(row => row.Operation == "provider-evidence-decoded")).GoalId);
        Assert.Equal(Active, Assert.Single(observations.Where(row => row.Operation == "requested")).GoalId);
    }

    [Theory]
    [InlineData("xAI")]
    [InlineData("Prøvider")]
    public async Task History_SelectiveReadPreservesCanonicalCandidates(string provider)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        await Seed(fixture.Workspace);
        using var connection = Open(fixture.Workspace);
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE model_fit_history SET provider_name = $provider";
        command.Parameters.AddWithValue("$provider", provider);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        command.Parameters.Clear();
        command.CommandText = """
            INSERT INTO model_fit_history (goal_id,task_id,role,provider_name,model_name,outcome,self_rating,timestamp)
            VALUES ('unrelated','unrelated','Developer','other','fixture','Completed','adequate','2026-10-17T11:10:00.0000000+00:00')
            """;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        var observations = new List<GoalSnapshotReadObservation>();
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.Workspace.SqliteStatePath, observations.Add);
        var full = await repository.ListModelFitHistoryAsync();
        var selective = await repository.ListProviderHoldHistoryAsync(TaskOutcomeRules.ProviderBudgetExhausted.Token);
        var working = new HashSet<string>(StringComparer.Ordinal) { Active, Failed };
        Assert.Equal(ProviderHoldHistory.SeedCandidates(full, working), ProviderHoldHistory.SeedCandidates(selective, working));
        Assert.Equal(new[] { Completed }, ProviderHoldHistory.SeedCandidates(selective, working));
        Assert.Equal(provider.All(char.IsAscii) ? 2 : 3, selective.Count);
        Assert.Equal(2, observations.Count(row => row.Operation == "model-fit-history"));
    }

    private static string Status(OrchestratorWorkspace workspace, Action<GoalSnapshotReadObservation>? observer)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "status"], workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => At,
            mainCommit: () => null, time: new FixedTime(), output: output, error: error, readObserver: observer));
        Assert.Equal("", error.ToString());
        return output.ToString();
    }

    private static async Task Seed(OrchestratorWorkspace workspace)
    {
        Directory.CreateDirectory(workspace.OrchestratorDirectory);
        StateDbMigrations.EnsureUpToDate(workspace.SqliteStatePath);
        using var connection = Open(workspace);
        foreach (var (id, status) in new[] { (Active, GoalStatus.Active), (Failed, GoalStatus.Failed), (Completed, GoalStatus.Completed) })
        {
            var dispatch = new TaskDispatchSnapshot("fixture", "fixture", @"C:\fixture", At,
                ProviderName: "xAI", ModelName: "fixture");
            var verification = new TaskVerificationSnapshot("fixture", @"C:\fixture", 1, "", "Provider budget exhausted.",
                At.AddMinutes(1), ProviderFailureKind: ProviderFailureKind.BudgetExhausted,
                DispatchStartedAt: At, CompletionVerdictVerifiedSuccess: false);
            var task = new TaskSnapshot(id, "Fixture", AgentRole.Developer,
                status == GoalStatus.Active ? WorkTaskStatus.Pending : WorkTaskStatus.Completed, null, null,
                status == GoalStatus.Active ? null : verification, status == GoalStatus.Active ? [] : [verification],
                status == GoalStatus.Active ? null : dispatch, null,
                DispatchHistory: status == GoalStatus.Active ? [] : [dispatch], WorkerBuildInvocations: []);
            var snapshot = new GoalSnapshot(id, "Fixture", status, [task], []);
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO goals (id,status,objective,snapshot_json,version,updated_at)
                VALUES ($id,$status,'Fixture',$json,1,$at)
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$status", status.ToString());
            command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(snapshot, Options));
            command.Parameters.AddWithValue("$at", At.ToString("O"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            if (status == GoalStatus.Active) continue;
            command.Parameters.Clear();
            command.CommandText = """
                INSERT INTO model_fit_history (goal_id,task_id,role,provider_name,model_name,outcome,self_rating,timestamp,outcome_rule)
                VALUES ($id,$id,'Developer','xAI','fixture','Failed','adequate',$at,'provider-budget-exhausted')
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$at", At.AddMinutes(1).ToString("O"));
            await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    private static SqliteConnection Open(OrchestratorWorkspace workspace)
    {
        var connection = new SqliteConnection($"Data Source={workspace.SqliteStatePath};Pooling=False");
        connection.Open();
        return connection;
    }

    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At.AddMinutes(2);
    }
}
