using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceDeclaredMethodFact(
    string Name,
    ImmutableArray<FocusedEvidenceAttributeUsageFact> Attributes);
