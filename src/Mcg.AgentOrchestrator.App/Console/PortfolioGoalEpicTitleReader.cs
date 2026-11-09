using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// One read-only membership snapshot per board build; never creates or migrates the store.
internal sealed class PortfolioGoalEpicTitleReader(string path)
{
    internal async Task<IReadOnlyDictionary<string, string>> ReadAsync(
        IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (goalIds.Count == 0 || !File.Exists(path)) return titles;
        try
        {
            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1 }.ToString());
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            // Read the full table to avoid a parameter limit for large boards.
            command.CommandText = """
                SELECT m.goal_id, e.title FROM goal_epic_memberships m
                JOIN epics e ON e.id = m.epic_id;
                """;
            var requested = goalIds.ToHashSet(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                if (requested.Contains(reader.GetString(0))) titles[reader.GetString(0)] = reader.GetString(1);
            return titles;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }
    }
}
