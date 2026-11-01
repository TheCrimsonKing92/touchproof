using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorLoopFirstTickRecord(
    DateTimeOffset Timestamp,
    [property: JsonPropertyName("elapsed_ms")] long? ElapsedMs,
    [property: JsonPropertyName("process_started")] DateTimeOffset? ProcessStarted,
    [property: JsonPropertyName("sweep_total_ms")] long? SweepTotalMs)
{
    public string EventKind => "loop-first-tick";
    public string? GoalId => null;
    public string Detail => "LOOP_FIRST_TICK";
    public string? Operator => ConductEventOperatorClassifier.Classify(EventKind, Detail);
}
