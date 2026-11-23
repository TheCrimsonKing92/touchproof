using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A proof belongs to the caller's SQLite read transaction. The schema cookie also
// invalidates a warm proof if coverage was removed and restored between acquisitions.
internal readonly record struct BacklogSnapshotRevision(string StoreId, string Revision, long SchemaCookie)
{
    private const string Table = "backlog_snapshot_revision";
    private static readonly (string Type, string Name, string Table, string Sql)[] Triggers = (
        from table in new[] { "backlog", "backlog_notes", "backlog_dependencies" }
        from operation in new[] { "insert", "update", "delete" }
        let name = $"{Table}_{table}_{operation}"
        select ("trigger", name, table, $"""
            CREATE TRIGGER {name} AFTER {operation} ON {table}
            BEGIN
                UPDATE {Table} SET revision = lower(hex(randomblob(16))) WHERE singleton = 1;
                SELECT CASE WHEN changes() <> 1 THEN RAISE(ABORT, 'Missing backlog snapshot revision') END;
            END
            """.ReplaceLineEndings("\n"))).ToArray();

    // Incremental SQLite BLOB writes can change TEXT without firing UPDATE triggers.
    // SQLite refuses writable blob handles for indexed columns. These partial indexes
    // stay empty and cover every projected column except INTEGER PRIMARY KEY aliases.
    private static readonly (string Type, string Name, string Table, string Sql)[] BlobWriteGuards = (
        from projection in new[]
        {
            (Table: "backlog", Columns: "id, title, body, status, created_at, updated_at, source_goal_id, priority, tags, superseded_by, superseded_at, pinned_at, bundled_reason"),
            (Table: "backlog_notes", Columns: "backlog_item_id, created_at, text"),
            (Table: "backlog_dependencies", Columns: "dependent_id, prerequisite_id, prerequisite_kind, created_at")
        }
        let name = $"{Table}_{projection.Table}_blob_guard"
        select ("index", name, projection.Table, $"CREATE INDEX {name} ON {projection.Table} ({projection.Columns}) WHERE 0")).ToArray();

    private static readonly (string Type, string Name, string Table, string Sql)[] Coverage = [.. Triggers, .. BlobWriteGuards];

    private static readonly string ReadSql = $"""
        SELECT store_id, revision, schema_version
        FROM {Table}, pragma_schema_version
        WHERE singleton = 1
          AND typeof(store_id) = 'text' AND length(store_id) = 32 AND store_id NOT GLOB '*[^0-9a-f]*'
          AND typeof(revision) = 'text' AND length(revision) = 32 AND revision NOT GLOB '*[^0-9a-f]*'
          AND (SELECT version FROM store_schema_versions WHERE store_name = 'backlog') = {StoreSchemaRegistry.Backlog.CurrentVersion}
          AND (SELECT count(*) FROM main.sqlite_schema
               WHERE (type, name, tbl_name, sql) IN (VALUES
               {string.Join(",\n", Coverage.Select(obj => $"('{obj.Type}', '{obj.Name}', '{obj.Table}', '{obj.Sql.Replace("'", "''", StringComparison.Ordinal)}')"))}
               )) = {Coverage.Length}
        """;

    internal static BacklogSnapshotRevision? TryRead(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = ReadSql;
            using var reader = command.ExecuteReader();
            return reader.Read() ? new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)) : null;
        }
        catch (SqliteException)
        {
            // Missing, unsupported or unreadable proof is handled by the ordinary reader.
            return null;
        }
    }

    // Called only under BacklogStore's migration write reservation. Rotating both
    // values makes repairing lost coverage invalidate earlier proofs.
    internal static void Install(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        foreach (var obj in Coverage)
        {
            command.CommandText = $"DROP {obj.Type} IF EXISTS {obj.Name}";
            command.ExecuteNonQuery();
        }
        command.CommandText = $"""
            DROP TABLE IF EXISTS {Table};
            CREATE TABLE {Table} (
                singleton INTEGER PRIMARY KEY CHECK (singleton = 1),
                store_id TEXT NOT NULL,
                revision TEXT NOT NULL
            );
            INSERT INTO {Table} VALUES (1, lower(hex(randomblob(16))), lower(hex(randomblob(16))));
            """;
        command.ExecuteNonQuery();
        foreach (var obj in Coverage)
        {
            command.CommandText = obj.Sql;
            command.ExecuteNonQuery();
        }
    }
}
