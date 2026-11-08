namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class Gate(string[] members, DateTimeOffset start)
{
    internal string[] Members { get; set; } = members;
    internal string? ExecutionId { get; init; }
    internal string? OuterAttemptId { get; init; }
    internal string? TrainIdentity { get; set; }
    internal string? Participation { get; set; }
    internal DateTimeOffset? AdmittedAt { get; set; }
    internal string? FinalOutcome { get; set; }
    internal DateTimeOffset? FinishedAt { get; set; }
    internal DateTimeOffset Start { get; } = start;
    internal DateTimeOffset Last { get; set; } = start;
    internal bool Live { get; set; } = true;
    internal List<Observation> Events { get; } = [];
}
