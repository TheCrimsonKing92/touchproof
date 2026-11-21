namespace Mcg.AgentOrchestrator.Core;

public sealed record GoalCreationOperatorIntentPayload(
    string RequestKey,
    string Fingerprint,
    string Mode,
    string Objective,
    string? RequestedEpicId,
    IReadOnlyList<string> CreationArguments,
    int? BriefFileValueIndex,
    IReadOnlyList<AgentDefinition> Agents,
    bool AllowTerminalSourceClaimTakeover);
