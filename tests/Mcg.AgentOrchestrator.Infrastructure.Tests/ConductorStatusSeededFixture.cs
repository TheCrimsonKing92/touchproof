using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Each instance owns its workspace and databases; no ambient clock, git or process lookup.
internal sealed class ConductorStatusSeededFixture : IDisposable
{
    internal const int CompletedCount = 200;
    internal const int PayloadCharacters = 32_768;
    internal const string Failed = "11111111111111111111111111111111";
    internal const string OtherFailed = "22222222222222222222222222222222";
    internal static readonly string[] NonTerminal =
    [
        "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
        "cccccccccccccccccccccccccccccccc", "dddddddddddddddddddddddddddddddd",
        "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
    ];
    internal static readonly DateTimeOffset At = new(2026, 10, 17, 11, 7, 0, TimeSpan.Zero);
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() } };
    private readonly ConductorVerbStartTests.Fixture _workspace = new();
    internal OrchestratorWorkspace Workspace => _workspace.Workspace;
    internal static string CompletedId(int index) => index.ToString("x32", CultureInfo.InvariantCulture);
    internal static GoalId[] EvidenceIds => [new(Failed), new(OtherFailed), new(CompletedId(1)), new(CompletedId(2)), new(CompletedId(3))];

    internal async Task Seed()
    {
        Directory.CreateDirectory(Workspace.OrchestratorDirectory);
        StateDbMigrations.EnsureUpToDate(Workspace.SqliteStatePath);
        using (var connection = Open(Workspace.SqliteStatePath))
        using (var transaction = connection.BeginTransaction())
        {
            for (var index = 1; index <= CompletedCount; index++)
            {
                var id = CompletedId(index);
                InsertGoal(connection, transaction, Snapshot(id, GoalStatus.Completed,
                    new string('x', PayloadCharacters), index <= 3 ? At.AddMinutes(-3) : null));
                // Large unrelated history must never be materialized by the status hold read.
                // History identity includes the timestamp, not the provider; keep attempts distinct.
                InsertHistory(connection, transaction, id, "other", null, new string('h', 8_192), At.AddMinutes(-4));
                if (index <= 3) InsertHistory(connection, transaction, id, "xAI", "provider-budget-exhausted", "fixture", At.AddMinutes(-3));
            }
            var statuses = new[] { GoalStatus.Draft, GoalStatus.Active, GoalStatus.Verified, GoalStatus.WaitingForHuman, GoalStatus.Draft };
            for (var index = 0; index < NonTerminal.Length; index++)
            {
                var snapshot = Snapshot(NonTerminal[index], statuses[index], "Visible task", null);
                if (index == 2)
                    snapshot = snapshot with { LandingBlocker = new("landing-completion/criterion-evidence-hold",
                        "operator evidence outstanding", "evidence:operator", At, "evidence-owner", "criterion-evidence-record") };
                InsertGoal(connection, transaction, snapshot);
            }
            InsertGoal(connection, transaction, Snapshot(Failed, GoalStatus.Failed, new string('f', PayloadCharacters), At.AddMinutes(1)));
            InsertGoal(connection, transaction, Snapshot(OtherFailed, GoalStatus.Failed, new string('f', PayloadCharacters), null));
            InsertHistory(connection, transaction, Failed, "xAI", "provider-budget-exhausted", "fixture", At.AddMinutes(1));
            transaction.Commit();
        }
        var store = CollaborationItemStore.ForDirectory(Workspace.OrchestratorDirectory);
        var item = await store.RaiseAsync(CollaborationItemType.Clarification, NonTerminal[0], "Which scope?", "Choose scope",
            $"spec-clarification:{NonTerminal[0]}:seeded-scope", TestContext.Current.CancellationToken);
        using var collaboration = Open(Path.Combine(Workspace.OrchestratorDirectory, "collaboration-items.db"));
        using var command = collaboration.CreateCommand();
        command.CommandText = "UPDATE collaboration_items SET raised_at = $at WHERE id = $id";
        command.Parameters.AddWithValue("$at", At.ToString("O"));
        command.Parameters.AddWithValue("$id", item.Id);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal string Status(bool json = false, Action<GoalSnapshotReadObservation>? observer = null)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using var error = new StringWriter(CultureInfo.InvariantCulture);
        Assert.Equal(0, CliConductorCommand.Run(json ? ["conductor", "status", "--json"] : ["conductor", "status"],
            Workspace, lockProbe: new ConductorVerbStartTests.FixedProbe(null), bootTime: () => DateTimeOffset.UnixEpoch,
            mainCommit: () => "fixture", output: output, error: error, time: new FixedTime(), readObserver: observer));
        Assert.Equal("", error.ToString());
        return output.ToString();
    }

    private static GoalSnapshot Snapshot(string id, GoalStatus status, string description, DateTimeOffset? exhaustedAt)
    {
        TaskDispatchSnapshot? dispatch = exhaustedAt is { } at
            ? new("fixture", "fixture", @"C:\fixture", at.AddMinutes(-1), ProviderName: "xAI", ModelName: "fixture") : null;
        TaskVerificationSnapshot? verification = exhaustedAt is { } completed
            ? new("fixture", @"C:\fixture", 1, "", "Provider budget exhausted.", completed,
                ProviderFailureKind: ProviderFailureKind.BudgetExhausted, DispatchStartedAt: completed.AddMinutes(-1),
                CompletionVerdictVerifiedSuccess: false) : null;
        var task = new TaskSnapshot(id, description, AgentRole.Developer,
            status is GoalStatus.Completed or GoalStatus.Failed ? WorkTaskStatus.Completed : WorkTaskStatus.Pending,
            null, null, verification, verification is null ? [] : [verification], dispatch, null,
            DispatchHistory: dispatch is null ? [] : [dispatch],
            LatestProviderBudgetRecoveryAt: status == GoalStatus.Completed && exhaustedAt is not null ? At.AddMinutes(-1) : null,
            WorkerBuildInvocations: []);
        return new(id, "Seeded goal", status, [task], []);
    }

    private static void InsertGoal(SqliteConnection connection, SqliteTransaction transaction, GoalSnapshot goal)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO goals (id,status,objective,snapshot_json,version,updated_at) VALUES ($id,$status,$objective,$json,1,$at)";
        command.Parameters.AddWithValue("$id", goal.Id);
        command.Parameters.AddWithValue("$status", goal.Status.ToString());
        command.Parameters.AddWithValue("$objective", goal.Objective);
        command.Parameters.AddWithValue("$json", JsonSerializer.Serialize(goal, Options));
        command.Parameters.AddWithValue("$at", At.ToString("O"));
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private static void InsertHistory(SqliteConnection connection, SqliteTransaction transaction, string id,
        string provider, string? rule, string shape, DateTimeOffset timestamp)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO model_fit_history (goal_id,task_id,role,provider_name,model_name,task_shape,outcome,self_rating,timestamp,outcome_rule)
            VALUES ($id,$id,'Developer',$provider,'fixture',$shape,'Failed','adequate',$at,$rule)
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$shape", shape);
        command.Parameters.AddWithValue("$at", timestamp.ToString("O"));
        command.Parameters.AddWithValue("$rule", (object?)rule ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    internal static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        return connection;
    }

    public void Dispose() => _workspace.Dispose();
    private sealed class FixedTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => At.AddMinutes(2);
    }
}
