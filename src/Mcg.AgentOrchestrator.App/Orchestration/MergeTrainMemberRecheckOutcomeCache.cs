using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Keep receipt-bound state in the existing train database, not in logs or scratch files.
internal sealed class MergeTrainMemberRecheckOutcomeCache(string databasePath) : MergeTrainMemberRecheck.IOutcomeCache
{
    public MergeTrainMemberRecheck.Outcome? Read(MergeTrainReceipt receipt)
    {
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT outcome_json FROM merge_train_member_rechecks WHERE receipt_id=$receipt AND train_id=$train;";
        command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
        command.Parameters.AddWithValue("$train", receipt.Identity.Value);
        if (command.ExecuteScalar() is not string json) return null;
        var outcome = JsonSerializer.Deserialize<MergeTrainMemberRecheck.Outcome>(json)
            ?? throw new InvalidDataException("Stored member recheck outcome is missing.");
        MergeTrainMemberRecheck.Validate(receipt, outcome);
        return outcome;
    }

    public bool TrySave(MergeTrainReceipt receipt, MergeTrainMemberRecheck.Outcome outcome)
    {
        MergeTrainMemberRecheck.Validate(receipt, outcome);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO merge_train_member_rechecks(receipt_id, train_id, outcome_json)
            VALUES($receipt, $train, $outcome);
            """;
        command.Parameters.AddWithValue("$receipt", receipt.ReceiptId);
        command.Parameters.AddWithValue("$train", receipt.Identity.Value);
        command.Parameters.AddWithValue("$outcome", JsonSerializer.Serialize(outcome));
        return command.ExecuteNonQuery() == 1;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = databasePath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA busy_timeout=5000;
                CREATE TABLE IF NOT EXISTS merge_train_member_rechecks(
                    receipt_id TEXT NOT NULL, train_id TEXT NOT NULL, outcome_json TEXT NOT NULL,
                    PRIMARY KEY(receipt_id, train_id));
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch { connection.Dispose(); throw; }
    }
}
