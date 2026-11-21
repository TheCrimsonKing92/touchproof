using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MainFullVerificationStore(string path)
{
    private const string EventType = "conductor.main-full-verification";
    private readonly SqliteRunEventStore _events = new(path, ensureSchema: false);

    internal bool Contains(string commit, string operation) =>
        _events.ReadByEventIdAsync(EventId(commit, operation)).GetAwaiter().GetResult() is not null;

    internal void Write(string operation, MainFullVerificationRecord record, DateTimeOffset now) =>
        _events.AppendAsync(new(EventType, null, operation, operation == "completed" ? record.Result?.Outcome : null,
            null, JsonSerializer.Serialize(record), now, EventId(record.Commit, operation))).GetAwaiter().GetResult();

    internal MainFullVerificationRecord? Latest(string operation, string? outcome = null)
    {
        if (!File.Exists(path)) return null;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM run_events
            WHERE event_type = $type AND operation = $operation AND ($outcome IS NULL OR status = $outcome)
            ORDER BY seq DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$type", EventType);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$outcome", (object?)outcome ?? DBNull.Value);
        return command.ExecuteScalar() is string payload
            ? JsonSerializer.Deserialize<MainFullVerificationRecord>(payload)
                ?? throw new InvalidDataException("Main full verification record is unreadable.")
            : null;
    }

    private static string EventId(string commit, string operation) => $"main-full-verification:{commit}:{operation}";
}
