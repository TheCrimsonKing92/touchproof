namespace Mcg.AgentOrchestrator.Infrastructure;

// Candidate-local dependency evidence, distinct from snapshot call-graph readers.
internal sealed record ImpactReplayDeclaredGuard(
    string Project, string ClassName, string DependencyKind, string DeclarationPath,
    IReadOnlyList<string> TriggerPaths, string Reason);

internal sealed record ImpactReplayDeclaredGuardReport(
    IReadOnlyList<ImpactReplayDeclaredGuard> Selections, string? DegradationReason = null);
