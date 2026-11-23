namespace Mcg.AgentOrchestrator.App.Orchestration;

public sealed record BatchTickSummary(
    int Tick,
    int Advanced,
    int Held,
    int Escalated,
    int Retried,
    int Done,
    bool WatchSleeping)
{
    public IReadOnlyList<string>? ProgressLines { get; init; }
    public IReadOnlyList<ConductorOperatorDispositionSnapshot>? OperatorDispositions { get; init; }
    public long? GoalSnapshotDeserializedUtf8Bytes { get; init; }
    public long? GoalSnapshotSerializedUtf8Bytes { get; init; }
}
