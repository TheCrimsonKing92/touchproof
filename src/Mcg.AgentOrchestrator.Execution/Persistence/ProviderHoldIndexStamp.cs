using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal readonly record struct ProviderHoldIndexStamp(long IndexVersion, long Generation, long HeaderGeneration)
{
    internal const int CurrentIndexVersion = 1;
    internal const string ReadSql = """
        SELECT provider_hold_index_stamp.index_version, provider_hold_index_stamp.generation, pragma_user_version.user_version
        FROM provider_hold_index_stamp CROSS JOIN pragma_user_version
        WHERE provider_hold_index_stamp.singleton = 1
        """;

    internal bool IsValid => IndexVersion == CurrentIndexVersion && Generation > 0 && Generation == HeaderGeneration;

    internal static ProviderHoldIndexStamp? TryRead(SqliteConnection connection, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = ReadSql;
            using var reader = command.ExecuteReader();
            return reader.Read() && reader.GetValue(0) is long version &&
                reader.GetValue(1) is long generation && reader.GetValue(2) is long header
                ? new(version, generation, header)
                : null;
        }
        catch (SqliteException)
        {
            return null; // A legacy or unreadable index is a miss.
        }
    }

    [StoreSchemaSource("orchestrator-state", "src/Mcg.AgentOrchestrator.Execution/Persistence/ProviderHoldIndexStamp.cs")]
    internal static async Task CreateSchemaAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS provider_hold_index_stamp (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                index_version INTEGER NOT NULL,
                generation INTEGER NOT NULL
            )
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    // Both the stamp row and SQLite's independent header value travel with a backup.
    // The caller holds the same write transaction that upserts the evidence.
    internal static async Task AdvanceAsync(SqliteConnection connection, CancellationToken token)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 1;
        command.CommandText = "PRAGMA user_version";
        var current = (long)(await command.ExecuteScalarAsync(token))!;
        var next = current is >= 0 and < int.MaxValue ? current + 1 : 1;
        command.CommandText = "PRAGMA user_version = " + next.ToString(CultureInfo.InvariantCulture);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = """
            INSERT INTO provider_hold_index_stamp (singleton, index_version, generation)
            VALUES (1, $version, $generation)
            ON CONFLICT(singleton) DO UPDATE SET index_version = excluded.index_version,
                generation = excluded.generation
            """;
        command.Parameters.AddWithValue("$version", CurrentIndexVersion);
        command.Parameters.AddWithValue("$generation", next);
        await command.ExecuteNonQueryAsync(token);
    }
}
