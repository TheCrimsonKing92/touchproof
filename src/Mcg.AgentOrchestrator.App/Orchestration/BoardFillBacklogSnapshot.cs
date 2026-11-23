using System.Globalization;
using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Snapshot only: never instantiate BacklogStore, whose constructor writes schema.
internal static class BoardFillBacklogSnapshot
{
    private const string ItemColumns = "id, title, body, status, created_at, updated_at, source_goal_id, priority, tags";
    private const string NoteColumns = "backlog_item_id, created_at, text";
    private const string DependencyColumns = "dependent_id, prerequisite_id, prerequisite_kind, created_at";
    private const string ItemOrder = "rowid";
    private const string NoteOrder = "created_at, id";
    private const string DependencyOrder = "sequence";

    private static readonly string ContentSql = $"""
        SELECT {JsonProjection("backlog", $"{ItemColumns}, pinned_at", ItemOrder)},
               {JsonProjection("backlog_notes", NoteColumns, NoteOrder)},
               {JsonProjection("backlog_dependencies", DependencyColumns, DependencyOrder)}
        """;

    // A writer can disable triggers on its own connection. Validate the ordered
    // projection in the same SQLite snapshot before trusting a matching revision.
    internal static string? TryReadContentHash(SqliteConnection connection)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = ContentSql;
            using var reader = command.ExecuteReader();
            if (!reader.Read()) return null;

            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            // Each complete JSON array delimits its own table, including empty tables.
            for (var column = 0; column < reader.FieldCount; column++)
                hash.AppendData(reader.GetFieldValue<byte[]>(column));
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (SqliteException)
        {
            return null;
        }
    }

    private static string JsonProjection(string table, string columns, string order) =>
        $"""
        (SELECT CAST(json_group_array(json_array({columns})) AS BLOB)
         FROM (SELECT {columns} FROM {table} ORDER BY {order}))
        """;

    internal static IReadOnlyList<BacklogItem> Read(string path, Action<string, SqliteConnection>? observe = null)
    {
        if (!File.Exists(path)) return [];
        using var session = new SqliteReadSession(path, observe);
        return ReadCore(session.Connection);
    }

    internal static IReadOnlyList<BacklogItem> ReadScoped(SqliteConnection connection, IEnumerable<string> ids,
        CancellationToken cancellationToken)
    {
        var scope = ids.Distinct(StringComparer.Ordinal).ToArray();
        var items = new Dictionary<string, BacklogItem>(StringComparer.Ordinal);
        foreach (var batch in scope.Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in ReadCore(connection, batch, cancellationToken)) items[item.Id] = item;
        }
        var prerequisites = items.Values.SelectMany(item => item.Dependencies)
            .Where(dependency => dependency.TargetKind == BacklogDependencyTargetKind.Backlog)
            .Select(dependency => dependency.PrerequisiteId).Distinct(StringComparer.Ordinal)
            .Except(scope, StringComparer.Ordinal).ToArray();
        foreach (var batch in prerequisites.Chunk(400))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in ReadCore(connection, batch, cancellationToken)) items[item.Id] = item;
        }
        return items.Values.ToArray();
    }

    internal static IReadOnlyList<BacklogItem> ReadCore(SqliteConnection connection, string[]? ids = null,
        CancellationToken cancellationToken = default, bool retain = false)
    {
        var items = new Dictionary<string, BacklogItem>(StringComparer.Ordinal);
        using var probe = connection.CreateCommand();
        probe.CommandText = "SELECT COUNT(*) FROM pragma_table_info('backlog') WHERE name = 'pinned_at'";
        var pinColumn = Convert.ToInt32(probe.ExecuteScalar(), CultureInfo.InvariantCulture) != 0 ? "pinned_at" : "NULL";
        using (var command = connection.CreateCommand())
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = $"SELECT {ItemColumns}, {pinColumn} FROM backlog";
            command.CommandText += ids is null
                ? $" ORDER BY {ItemOrder}"
                : $" WHERE id IN ({SqliteReadSession.BindIds(command, ids)})";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var item = new BacklogItem(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    Enum.Parse<BacklogItemStatus>(reader.GetString(3)), Parse(reader.GetString(4)),
                    Parse(reader.GetString(5)), reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7), reader.GetString(8),
                    PinnedAt: reader.IsDBNull(9) ? null : Parse(reader.GetString(9)));
                items.Add(item.Id, item);
            }
        }
        var notes = new Dictionary<string, List<BacklogNote>>(StringComparer.Ordinal);
        var dependencies = new Dictionary<string, List<BacklogDependency>>(StringComparer.Ordinal);
        using (var command = connection.CreateCommand())
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = $"SELECT {NoteColumns} FROM backlog_notes";
            if (ids is not null) command.CommandText += $" WHERE backlog_item_id IN ({SqliteReadSession.BindIds(command, ids)})";
            command.CommandText += $" ORDER BY {NoteOrder}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = reader.GetString(0);
                if (!notes.TryGetValue(id, out var list)) notes.Add(id, list = []);
                list.Add(new(Parse(reader.GetString(1)), reader.GetString(2)));
            }
        }
        using (var command = connection.CreateCommand())
        {
            cancellationToken.ThrowIfCancellationRequested();
            command.CommandText = $"SELECT {DependencyColumns} FROM backlog_dependencies";
            if (ids is not null) command.CommandText += $" WHERE dependent_id IN ({SqliteReadSession.BindIds(command, ids)})";
            command.CommandText += $" ORDER BY {DependencyOrder}";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var id = reader.GetString(0);
                if (!dependencies.TryGetValue(id, out var list)) dependencies.Add(id, list = []);
                list.Add(new(id, reader.GetString(1), Enum.Parse<BacklogDependencyTargetKind>(reader.GetString(2)),
                    Parse(reader.GetString(3))));
            }
        }
        var snapshot = items.Values.Select(item => item with
        {
            Notes = notes.TryGetValue(item.Id, out var noteList) ? retain ? noteList.AsReadOnly() : noteList : [],
            Dependencies = dependencies.TryGetValue(item.Id, out var dependencyList) ? retain ? dependencyList.AsReadOnly() : dependencyList : []
        }).ToArray();
        return retain ? Array.AsReadOnly(snapshot) : snapshot;
    }

    private static DateTimeOffset Parse(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}
