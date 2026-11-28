using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceUsingDirectiveFact(
    [property: JsonRequired] string? Name,
    [property: JsonRequired] bool HasAlias,
    [property: JsonRequired] bool IsGlobal);
