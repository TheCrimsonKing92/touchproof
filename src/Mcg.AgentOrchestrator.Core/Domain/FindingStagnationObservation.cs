namespace Mcg.AgentOrchestrator.Core;

/// <summary>One authoritative raising observation per finding and Developer round.</summary>
public sealed record FindingStagnationObservation(
    string RoundKey,
    TaskId TaskId,
    AgentRole Role,
    string Description,
    string NormalizedDescription,
    IReadOnlyList<string> SecondaryDescriptions,
    IReadOnlyList<string> CitedFiles,
    DateTimeOffset RaisedAt,
    TaskVerificationRecord Verification);
