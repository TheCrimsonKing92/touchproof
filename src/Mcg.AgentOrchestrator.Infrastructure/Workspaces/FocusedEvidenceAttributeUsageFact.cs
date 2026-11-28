using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceAttributeUsageFact(
    string Name,
    [property: JsonRequired] bool HasTwoStringLiteralArguments,
    string? FirstArgument,
    string? SecondArgument);
