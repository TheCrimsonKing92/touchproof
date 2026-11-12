namespace Mcg.AgentOrchestrator.App.Orchestration;

// Called only under the retention lease; status readers never invoke measurement.
internal sealed class StorageHealthSnapshotWriter(
    Func<string, IReadOnlyList<OrchestratorArtifactRegistry.Entry>, StorageHealthMeasurement.Report>? measure = null,
    IReadOnlyList<OrchestratorArtifactRegistry.Entry>? entries = null)
{
    internal const string FileName = "storage-health-snapshot.json";
    internal static StorageHealthSnapshotWriter Default { get; } = new();

    internal void Publish(string orchestratorDirectory, DateTimeOffset now,
        List<EvidenceRetentionDecision> decisions)
    {
        if (!Directory.Exists(orchestratorDirectory)) return;
        string? temporaryPath = null;
        var path = Path.Combine(orchestratorDirectory, FileName);
        try
        {
            var report = (measure ?? StorageHealthMeasurement.Build)(orchestratorDirectory,
                entries ?? OrchestratorArtifactRegistry.Entries);
            var snapshot = new StorageHealthSnapshot(report.Totals.Bytes, report.Totals.Files,
                report.Totals.Over, report.Totals.Unregistered,
                report.Totals.Over == 0 ? null : report.Classes.First(row => row.Status == "over").Pattern,
                now.ToUniversalTime());
            var parent = Directory.GetParent(Path.GetFullPath(orchestratorDirectory))
                ?? throw new IOException("Storage snapshot directory has no parent.");
            temporaryPath = Path.Combine(parent.FullName, $"storage-health-snapshot-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(temporaryPath, snapshot.Serialize());
            File.Move(temporaryPath, path, overwrite: true);
        }
        catch (Exception ex)
        {
            decisions.Add(new(EvidenceArtifactFamily.RunEvents, EvidenceRetentionAction.Preserved,
                path, null, EvidenceOwnerResolution.Unrecorded, "storage-health-snapshot-failed",
                FailureExceptionType: ex.GetType().Name));
        }
        finally
        {
            if (temporaryPath is not null)
                try { File.Delete(temporaryPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
