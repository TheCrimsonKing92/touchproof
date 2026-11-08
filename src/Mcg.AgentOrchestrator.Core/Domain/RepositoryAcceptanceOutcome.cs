namespace Mcg.AgentOrchestrator.Core;

/// <summary>The evidence-backed verdict for one repository in an aggregate attempt.</summary>
public sealed record RepositoryAcceptanceOutcome(string Key, bool Passed, string EvidenceReference, string? Detail);
