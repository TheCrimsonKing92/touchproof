using System.Text.Json;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

internal sealed class BoardFillSnapshotTestDatabase : IDisposable
{
    internal const string Timestamp = "2026-11-23T14:06:00.0000000+00:00";
    internal const string ItemId = "item-000001";
    internal const string NoteTrigger = "backlog_snapshot_revision_backlog_notes_update";
    private readonly string _directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"board-fill-{Guid.NewGuid():N}");
    internal string Path => At("backlog.db");
    internal string At(string name) => System.IO.Path.Combine(_directory, name);

    internal BoardFillSnapshotTestDatabase(int items = 2)
    {
        BacklogStore.Setup(Path);
        Run($"""
            WITH RECURSIVE numbers(n) AS (VALUES (1) UNION ALL SELECT n + 1 FROM numbers WHERE n < {items})
            INSERT INTO backlog (id, title, body, status, created_at, updated_at)
            SELECT printf('item-%06d', n), printf('Work item %06d', n), hex(zeroblob(128)), 'Open',
                '{Timestamp}', '{Timestamp}' FROM numbers;
            INSERT INTO backlog_notes (backlog_item_id, created_at, text)
            SELECT id, '{Timestamp}', 'Initial evidence' FROM backlog;
            INSERT INTO backlog_dependencies (dependent_id, prerequisite_id, prerequisite_kind, created_at)
            SELECT id, 'prerequisite', 'Goal', '{Timestamp}' FROM backlog WHERE rowid % 2 = 1;
            """);
    }

    internal SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path, Mode = SqliteOpenMode.ReadWrite, Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    internal void Run(string sql)
    {
        using var connection = Open();
        Execute(connection, sql);
    }

    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static void RemoveSnapshotProof(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT type, name FROM sqlite_schema WHERE name GLOB 'backlog_snapshot_revision_*'";
        var coverage = new List<(string Type, string Name)>();
        using (var reader = command.ExecuteReader())
        {
            while (reader.Read())
                coverage.Add((reader.GetString(0), reader.GetString(1)));
        }

        foreach (var (type, name) in coverage)
            Execute(connection, $"DROP {type} {name}");

        Execute(connection, "DROP TABLE backlog_snapshot_revision");
    }

    internal static string Mutation(string part, int value) => part switch
    {
        "item" => $"UPDATE backlog SET title = 'Title {value}' WHERE id = '{ItemId}'",
        "note" => $"UPDATE backlog_notes SET text = 'Evidence {value}' WHERE backlog_item_id = '{ItemId}'",
        "dependency" => $"UPDATE backlog_dependencies SET prerequisite_id = 'prerequisite-{value}' WHERE dependent_id = '{ItemId}'",
        _ => throw new ArgumentOutOfRangeException(nameof(part))
    };

    internal static string Contents(IReadOnlyList<BacklogItem> snapshot) => JsonSerializer.Serialize(snapshot);

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
