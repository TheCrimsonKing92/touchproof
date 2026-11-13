using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayReport(
    string FloorSource,
    IReadOnlyList<string> FloorLanes,
    IReadOnlyList<ImpactReplayAttemptRecord> Attempts,
    IReadOnlyList<ImpactReplayDiagnostic> SkippedInputs,
    IReadOnlyList<ImpactReplayDiagnostic> UnreplayableAttempts,
    IReadOnlyList<string> CleanupErrors,
    int AttemptsGainingChangedTestClasses = 0,
    ImpactReplayUnmappedTestClasses? UnmappedTestClasses = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ImpactReplayMeasurement? Measurement = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ImpactReplaySourceGuardReport? SourceReadingGuards = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ImpactReplayHistoryReadSummary? HistoricalEvidence = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ImpactReplayReportRun? Run = null);

internal sealed record ImpactReplayHistoryReadSummary(IReadOnlyList<string> QueriedIdentities,
    int MetadataReads, int TrxParses, IReadOnlyList<string> ParsedPaths);

internal sealed record ImpactReplayReportRun(string RunId, string CheckpointPath);
