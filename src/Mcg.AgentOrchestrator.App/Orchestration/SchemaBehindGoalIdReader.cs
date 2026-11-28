using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SchemaBehindGoalIdReader
{
    public static bool TryReadAll(IReadOnlyList<UnavailableGoalStore> stores, out IReadOnlyList<string> ids)
    {
        ids = [];
        if (stores.Any(store => store.Failure != "schema-behind"))
            return false;

        var allIds = new List<string>();
        foreach (var store in stores)
        {
            if (!TryReadGoalIds(store.Path, out var storeIds))
                return false;
            allIds.AddRange(storeIds);
        }

        ids = allIds;
        return true;
    }

    public static bool TryReadGoalIds(string path, out IReadOnlyList<string> ids)
    {
        ids = [];
        if (!File.Exists(path))
            return false;

        try
        {
            var readIds = new List<string>();
            using (var connection = StateDbConnectionFactory.Open(path, StateDbConnectionProfile.MigrationProbeRead,
                StateDbConnectionFactory.FastFailBusyTimeoutMilliseconds))
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id FROM goals";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    if (reader.IsDBNull(0))
                        return false;
                    readIds.Add(reader.GetString(0));
                }
            }

            ids = readIds;
            return true;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
    }
}
