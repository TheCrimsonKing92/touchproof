using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A receipt-bound fragment of the train store, migrated in its owner's transaction.
internal static class MergeTrainMemberRecheckStoreSetup
{
    [StoreSchemaSource("merge-train-acceptance", "src/Mcg.AgentOrchestrator.Execution/Persistence/MergeTrainMemberRecheckStoreSetup.cs")]
    internal static void EnsureSchema(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS merge_train_member_rechecks(
                receipt_id TEXT NOT NULL, train_id TEXT NOT NULL, outcome_json TEXT NOT NULL,
                PRIMARY KEY(receipt_id, train_id));
            """;
        command.ExecuteNonQuery();
    }
}
