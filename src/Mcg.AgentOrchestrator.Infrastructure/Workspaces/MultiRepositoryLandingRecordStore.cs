using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>Durable tuple landing outcomes and write-ahead progress. Unreadable state fails closed.</summary>
public sealed class MultiRepositoryLandingRecordStore(string orchestratorDirectory)
{
    public string DatabasePath { get; } = Path.Combine(orchestratorDirectory, "multi-repository-landings.db");

    public MultiRepositoryLandingRecord? ReadLanding(GoalId id) => Read<MultiRepositoryLandingRecord>(id, "landed");
    public MultiRepositoryLandingFailure? ReadFailure(GoalId id) => Read<MultiRepositoryLandingFailure>(id, "failed");
    public MultiRepositoryLandingAttempt? ReadAttempt(GoalId id) => Read<MultiRepositoryLandingAttempt>(id, "attempt");
    public void Write(MultiRepositoryLandingRecord record) => Write(record.GoalId, "landed", record);
    public void Write(MultiRepositoryLandingFailure failure) => Write(failure.GoalId, "failed", failure);
    public void Write(MultiRepositoryLandingAttempt attempt) => Write(attempt.GoalId, "attempt", attempt);

    private SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE IF NOT EXISTS landings (goal TEXT NOT NULL, kind TEXT NOT NULL, payload TEXT NOT NULL, PRIMARY KEY(goal, kind))";
        command.ExecuteNonQuery();
        return connection;
    }

    private T? Read<T>(GoalId id, string kind) where T : class
    {
        if (!File.Exists(DatabasePath)) return null;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT payload FROM landings WHERE goal=$goal AND kind=$kind";
        command.Parameters.AddWithValue("$goal", id.Value);
        command.Parameters.AddWithValue("$kind", kind);
        return command.ExecuteScalar() is string payload
            ? JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidDataException("Unreadable tuple landing state.") : null;
    }

    private void Write<T>(GoalId id, string kind, T value)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        // A completed landing is immutable; retries must never replace its accepted tuple.
        command.CommandText = kind == "landed"
            ? "INSERT INTO landings(goal,kind,payload) VALUES($goal,$kind,$payload)"
            : "INSERT INTO landings(goal,kind,payload) VALUES($goal,$kind,$payload) ON CONFLICT(goal,kind) DO UPDATE SET payload=excluded.payload";
        command.Parameters.AddWithValue("$goal", id.Value);
        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(value));
        command.ExecuteNonQuery();
    }
}
