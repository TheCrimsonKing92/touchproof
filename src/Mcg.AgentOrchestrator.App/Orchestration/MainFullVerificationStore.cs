using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class MainFullVerificationStore(string path)
{
    private const string EventType = "conductor.main-full-verification";
    private const int SqliteUniqueConstraint = 2067;
    private readonly SqliteRunEventStore _events = new(path, ensureSchema: false);

    internal bool Contains(string commit, string operation)
    {
        if (!File.Exists(path)) return false;
        using var connection = OpenReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1 FROM run_events
            WHERE event_id = $legacy OR (event_id >= $prefix AND event_id < $upper)
            LIMIT 1
            """;
        var legacy = LegacyEventId(commit, operation);
        command.Parameters.AddWithValue("$legacy", legacy);
        // ':' and ';' are adjacent in the event-id index's binary collation.
        command.Parameters.AddWithValue("$prefix", legacy + ":");
        command.Parameters.AddWithValue("$upper", legacy + ";");
        return command.ExecuteScalar() is not null;
    }

    internal MainFullVerificationRecord? Read(MainFullVerificationRecord admission, string operation)
    {
        using var connection = OpenReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM run_events
            WHERE event_id IN ($id, $legacy)
            ORDER BY seq DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", EventId(admission.Commit, operation, admission.Attempt));
        command.Parameters.AddWithValue("$legacy", admission.Attempt == 1
            ? LegacyEventId(admission.Commit, operation) : DBNull.Value);
        return ReadRecord(command);
    }

    internal MainFullVerificationRecord? ReserveAdmission(string commit, int previousAttempt,
        DateTimeOffset now, int intervalSeconds)
    {
        var record = new MainFullVerificationRecord(commit, now, intervalSeconds, Attempt: previousAttempt + 1);
        try
        {
            // The unique event id makes two reservations from the same admission snapshot contend for one row.
            Write("admitted", record, now);
            return record;
        }
        catch (SqliteException ex) when (ex.SqliteExtendedErrorCode == SqliteUniqueConstraint &&
            Read(record, "admitted") is not null)
        {
            return null;
        }
    }

    internal void Write(string operation, MainFullVerificationRecord record, DateTimeOffset now) =>
        _events.AppendAsync(new(EventType, null, operation, operation == "completed" ? record.Result?.Outcome : null,
            null, JsonSerializer.Serialize(record), now, EventId(record.Commit, operation, record.Attempt)))
            .GetAwaiter().GetResult();

    internal MainFullVerificationRecord? Latest(string operation, string? outcome = null)
    {
        if (!File.Exists(path)) return null;
        using var connection = OpenReadConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT payload_json FROM run_events
            WHERE event_type = $type AND operation = $operation AND ($outcome IS NULL OR status = $outcome)
            ORDER BY seq DESC LIMIT 1
            """;
        command.Parameters.AddWithValue("$type", EventType);
        command.Parameters.AddWithValue("$operation", operation);
        command.Parameters.AddWithValue("$outcome", (object?)outcome ?? DBNull.Value);
        return ReadRecord(command);
    }

    private SqliteConnection OpenReadConnection()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false
        }.ToString());
        try
        {
            connection.Open();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static MainFullVerificationRecord? ReadRecord(SqliteCommand command) =>
        command.ExecuteScalar() is string payload
            ? JsonSerializer.Deserialize<MainFullVerificationRecord>(payload)
                ?? throw new InvalidDataException("Main full verification record is unreadable.")
            : null;

    private static string EventId(string commit, string operation, int attempt) =>
        FormattableString.Invariant($"{LegacyEventId(commit, operation)}:{attempt}");

    private static string LegacyEventId(string commit, string operation) => $"main-full-verification:{commit}:{operation}";
}
