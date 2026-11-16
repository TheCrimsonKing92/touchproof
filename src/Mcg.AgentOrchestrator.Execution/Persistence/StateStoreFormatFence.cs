using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// The caller holds BEGIN IMMEDIATE through this check, any level stamp, and its write.
internal static class StateStoreFormatFence
{
    internal static void Enforce(SqliteConnection connection)
    {
        var entry = StoreSchemaRegistry.StateStoreFormat;
        var state = StoreSchemaVersions.Verify(connection, entry, out var recorded);
        if (state == StoreSchemaState.Current)
            return;

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_schema WHERE type = 'table' AND name = 'goals'";
        if (command.ExecuteScalar() is null)
            return;

        if (state == StoreSchemaState.Newer)
            throw new InvalidOperationException(
                $"State store recorded format level {recorded} exceeds this build's format level {entry.CurrentVersion}. " +
                "Use a build that understands the recorded format.");

        GoalTimelineStore.CreateSchema(connection);
        StoreSchemaVersions.UpgradeToCurrent(connection, entry);
    }
}
