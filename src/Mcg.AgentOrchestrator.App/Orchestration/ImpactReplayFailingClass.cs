namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayFailingClass(string ClassName, string? Lane, bool? WouldRun,
    string? FlakeEvidence = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    bool ConfirmedHardFailure = false)
{
    public bool IsMiss => Lane is not null && WouldRun == false && FlakeEvidence is null;
    public bool IsFlakyFailureInSkippedLane => Lane is not null && WouldRun == false && FlakeEvidence is not null;
}
