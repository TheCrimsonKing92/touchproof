using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerOccupancyReadException(string code, string message, Exception? inner = null)
    : IOException(message, inner)
{
    public string Code { get; } = code;
}

// Projects only the fields that define TaskProcess.IsRunning. Never loads a kernel or archive.
public static class SqliteWorkerOccupancyReader
{
    public static async Task<long> ReadAsync(string path, CancellationToken cancellationToken = default,
        Action<string>? statementObserver = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            _ = File.GetAttributes(path); // Unlike File.Exists, preserves access failures.
            using var connection = StateDbConnectionFactory.Open(path, StateDbConnectionProfile.QueryOnlyRead,
                statementObserver: statementObserver);
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            async Task<object?> Scalar(string sql)
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.CommandText = sql;
                statementObserver?.Invoke(sql);
                return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }
            var columns = Convert.ToInt64(await Scalar("SELECT COUNT(*) FROM pragma_table_info('meta') WHERE name IN ('key','value')"), CultureInfo.InvariantCulture);
            if (columns != 2 || Convert.ToInt64(await Scalar("SELECT COUNT(*) FROM pragma_table_info('goals') WHERE name = 'snapshot_json'"), CultureInfo.InvariantCulture) != 1)
                throw new WorkerOccupancyReadException("unsupported-schema", "Required census columns are unavailable.");
            if (!string.Equals((await Scalar("SELECT value FROM meta WHERE key = 'schema_version'"))?.ToString(),
                SqliteOrchestratorStateRepository.CurrentSchemaVersion, StringComparison.Ordinal))
                throw new WorkerOccupancyReadException("unsupported-schema", "State schema version is unsupported.");
            // CASE keeps SQLite JSON functions away from malformed input. Metadata-only goals have Tasks=[].
            if (Convert.ToInt64(await Scalar("""
                SELECT COUNT(*) FROM goals WHERE CASE
                    WHEN NOT json_valid(snapshot_json) THEN 1
                    WHEN json_type(snapshot_json) <> 'object' THEN 1
                    WHEN json_type(snapshot_json, '$.Tasks') IS NOT 'array' THEN 1
                    WHEN (SELECT COUNT(*) FROM json_each(snapshot_json) WHERE key = 'Tasks') <> 1 THEN 1
                    ELSE 0 END
                """), CultureInfo.InvariantCulture) != 0)
                throw Malformed();
            // Bounded-width rows and constant application memory, regardless of goal body size.
            command.CommandText = """
                SELECT task.type,
                    CASE WHEN task.type = 'object' THEN json_type(task.value, '$.LastProcess') END,
                    CASE WHEN task.type = 'object' THEN CASE
                        WHEN length(json_extract(task.value, '$.LastProcess.CompletedAt')) > 128 THEN 'oversized'
                        ELSE json_type(task.value, '$.LastProcess.CompletedAt') END END,
                    CASE WHEN task.type = 'object' THEN substr(json_extract(task.value, '$.LastProcess.CompletedAt'), 1, 128) END,
                    CASE WHEN task.type = 'object' THEN json_type(task.value, '$.LastProcess.ExitCode') END,
                    CASE WHEN task.type = 'object' THEN CASE WHEN json_type(task.value, '$.LastProcess.ExitCode') = 'integer'
                        THEN json_extract(task.value, '$.LastProcess.ExitCode') END END,
                    CASE WHEN task.type = 'object' THEN
                        (SELECT COUNT(*) FROM json_each(task.value) WHERE key = 'LastProcess') ELSE 0 END,
                    CASE WHEN task.type = 'object' THEN CASE WHEN json_type(task.value, '$.LastProcess') = 'object' THEN
                        (SELECT COUNT(*) FROM json_each(task.value, '$.LastProcess') WHERE key = 'CompletedAt') ELSE 0 END ELSE 0 END,
                    CASE WHEN task.type = 'object' THEN CASE WHEN json_type(task.value, '$.LastProcess') = 'object' THEN
                        (SELECT COUNT(*) FROM json_each(task.value, '$.LastProcess') WHERE key = 'ExitCode') ELSE 0 END ELSE 0 END
                FROM goals, json_each(goals.snapshot_json, '$.Tasks') AS task
                """;
            statementObserver?.Invoke(command.CommandText);
            await using var rows = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            long count = 0;
            while (await rows.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? Type(int column) => rows.IsDBNull(column) ? null : rows.GetString(column);
                static bool Null(string? type) => type is null or "null";
                if (Type(0) != "object" || rows.GetInt64(6) > 1 || rows.GetInt64(7) > 1 || rows.GetInt64(8) > 1)
                    throw Malformed();
                var process = Type(1);
                if (Null(process)) continue;
                if (process != "object") throw Malformed();
                var completed = Type(2);
                var exit = Type(4);
                if (!Null(completed) && (completed != "text" || !ValidTimestamp(rows.GetString(3)))) throw Malformed();
                if (!Null(exit) && (exit != "integer" || rows.GetInt64(5) is < int.MinValue or > int.MaxValue)) throw Malformed();
                // ConductorDriver counts every task, including shared PIDs and terminal task/goal statuses.
                if (Null(completed) && Null(exit)) count = checked(count + 1);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not WorkerOccupancyReadException)
        {
            var code = ex switch
            {
                FileNotFoundException or DirectoryNotFoundException => "missing-store",
                UnauthorizedAccessException or IOException or SqliteException => "unreadable-store",
                InvalidOperationException => "unsupported-schema",
                _ => "read-failed"
            };
            throw new WorkerOccupancyReadException(code, code switch
            {
                "missing-store" => "State store is missing.",
                "unreadable-store" => "State store cannot be read.",
                "unsupported-schema" => "State store does not support this census.",
                _ => "Recorded worker read failed."
            }, ex);
        }
    }

    private static WorkerOccupancyReadException Malformed() =>
        new("malformed-data", "Relevant task or process data is malformed.");

    private static bool ValidTimestamp(string value)
    {
        // Match the snapshot serializer's ISO timestamp contract, rather than permissive date parsing.
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(value));
        return document.RootElement.TryGetDateTimeOffset(out _);
    }
}
