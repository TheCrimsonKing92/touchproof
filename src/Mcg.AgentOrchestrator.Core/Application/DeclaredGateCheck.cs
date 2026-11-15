namespace Mcg.AgentOrchestrator.Core;

// A declared check's name and its host-owned selector matching contract.
public sealed record DeclaredGateCheck(string Name, Func<string, bool> Matches,
    string? Type = null, string? Command = null);
