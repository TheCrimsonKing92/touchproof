using System.Collections.ObjectModel;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum ImpactReplayPhase
{
    Startup, HistoryLoading, LedgerLoading, InventoryLoading, CaseSelection, Checkout,
    SourceAnalysis, EvidenceReading, ImpactAnalysis, CaseCompletion, Cleanup, Finalizing, Finished
}

internal enum ImpactReplayRunState { Running, Finalizing, Completed, Failed, Cancelled }
internal enum ImpactReplayCaseKind { Replayable, Skipped, Unreplayable }

// Observation is explicit and replay-local. A heartbeat is observation, never evidence of completed work.
internal interface IImpactReplaySink
{
    void Phase(ImpactReplayPhase phase) { }
    void InventoryDiscovered(int count) { }
    void InputExamined() { }
    void InventorySelected(int count) { }
    void BeginCase(ImpactReplayStoredAttempt attempt, int ordinal) { }
    void Diagnostic(ImpactReplayDiagnostic diagnostic) { }
    void CompleteCase(ImpactReplayCaseResult result) { }
    void OwnCheckout(string path) { }
    void CheckoutCleaned(string path, string status, string? error) { }
}

internal sealed class ImpactReplayNullSink : IImpactReplaySink
{
    internal static readonly IImpactReplaySink Instance = new ImpactReplayNullSink();
}

internal sealed record ImpactReplayRunConfiguration(string ExecutionDirectory, string OrchestratorDirectory,
    DateTimeOffset? Since, int RequestedLimit, IReadOnlyList<string>? Floor,
    string? CheckoutParent);

internal sealed record ImpactReplayRunIdentity(string RunId, DateTimeOffset StartedAt,
    ImpactReplayRunConfiguration Configuration, string Runtime, string Implementation, string SourceRevision);

internal sealed record ImpactReplayRunProgress(string RunId, string Kind, ImpactReplayPhase Phase,
    DateTimeOffset Timestamp, double RunElapsedSeconds, double PhaseElapsedSeconds,
    double LastProgressElapsedSeconds, int RequestedLimit, int? DiscoveredInputs = null,
    int ExaminedInputs = 0, int? EligibleAttempts = null, int ExaminedCases = 0, int Completed = 0,
    int Replayable = 0, int Skipped = 0, int Unreplayable = 0, int? CaseOrdinal = null,
    string? AttemptId = null, string? GoalId = null);

internal sealed record ImpactReplayCaseResult(ImpactReplayCaseKind Kind, ImpactReplayAttemptRecord? Record = null,
    ImpactReplayDiagnostic? Diagnostic = null,
    IReadOnlyList<ImpactReplayUnmappedTestClasses.Occurrence>? UnmappedClasses = null,
    bool Gained = false, IReadOnlyList<string>? GuardClasses = null);

internal sealed class ImpactReplayCaseContext
{
    internal string? Checkout { get; set; }
    internal bool Registered { get; set; }
    internal IReadOnlyList<string>? GuardClasses { get; set; }
    internal IReadOnlyList<string> FloorLanes { get; set; } = [];
}

// Kept outside the recoverable input exception taxonomy, including failures originating in a timer.
internal sealed class ImpactReplayObservationException(string message, Exception inner) : Exception(message, inner);

internal sealed class ImpactReplayDiagnostics(IImpactReplaySink sink) : Collection<ImpactReplayDiagnostic>
{
    protected override void InsertItem(int index, ImpactReplayDiagnostic item)
    {
        sink.Diagnostic(item);
        base.InsertItem(index, item);
    }
}
