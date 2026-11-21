using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The slot owner source validates the holder before asking which receipt step is pending.
internal static class ActivationAdmissionGroupedPendingStep
{
    internal static ActivationAdmissionOwnerPendingStep Read(string directory, ConductorGroupedGateAttempt attempt) =>
        attempt.Kind switch
        {
            "train" => ReadTrain(Path.Combine(directory, "merge-train-acceptance.db"), attempt.IdentityValue),
            "cohort" => ReadCohort(Path.Combine(directory, "cohort-acceptance.db"), attempt.IdentityValue),
            _ => ActivationAdmissionOwnerPendingStep.None
        };

    private static ActivationAdmissionOwnerPendingStep ReadTrain(string database, string identity)
    {
        if (ReceiptDatabaseMissing(database)) return ActivationAdmissionOwnerPendingStep.AwaitingReceipt;
        var store = MergeTrainAcceptanceStore.OpenReadOnly(database);
        var receipt = store.TryReadReceipt(identity);
        if (receipt is null) return ActivationAdmissionOwnerPendingStep.AwaitingReceipt;
        return receipt.Outcome switch
        {
            MergeTrainGateOutcome.Passed => ActivationAdmissionOwnerPendingStep.PassedReceipt,
            MergeTrainGateOutcome.Failed => receipt.GateAttemptId is { } gateAttempt &&
                store.ReadAttemptEjections(gateAttempt).Any(ejection => ejection.Reason is
                    MergeTrainEjectionReason.RedNewestMember or MergeTrainEjectionReason.RedAttributedMember)
                    ? ActivationAdmissionOwnerPendingStep.SurvivorsGate : ActivationAdmissionOwnerPendingStep.Attribution,
            _ => ActivationAdmissionOwnerPendingStep.None
        };
    }

    private static ActivationAdmissionOwnerPendingStep ReadCohort(string database, string identity)
    {
        if (ReceiptDatabaseMissing(database)) return ActivationAdmissionOwnerPendingStep.AwaitingReceipt;
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 1
        }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT outcome, attribution,
                EXISTS(SELECT 1 FROM cohort_partition_receipts p WHERE p.cohort_id=r.cohort_id)
            FROM cohort_receipts r WHERE cohort_id=$id;
            """;
        command.Parameters.AddWithValue("$id", identity);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return ActivationAdmissionOwnerPendingStep.AwaitingReceipt;
        return reader.GetString(0) switch
        {
            "Passed" => ActivationAdmissionOwnerPendingStep.PassedReceipt,
            "Failed" when reader.GetString(1) == nameof(AcceptanceCohortAttributionOutcome.NotApplicable) => reader.GetBoolean(2)
                ? ActivationAdmissionOwnerPendingStep.FocusedPartition : ActivationAdmissionOwnerPendingStep.Attribution,
            _ => ActivationAdmissionOwnerPendingStep.None
        };
    }

    private static bool ReceiptDatabaseMissing(string path)
    {
        // File.Exists also returns false for access failures. Leave those on the source's error path.
        try
        {
            _ = File.GetAttributes(path);
            return false;
        }
        catch (IOException ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return true;
        }
    }
}
