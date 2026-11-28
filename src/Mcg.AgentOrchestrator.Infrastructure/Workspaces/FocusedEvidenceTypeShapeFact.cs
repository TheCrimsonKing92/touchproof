using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record FocusedEvidenceTypeShapeFact(
    [property: JsonRequired] bool IsAbstract,
    [property: JsonRequired] bool HasTypeParameters,
    [property: JsonRequired] bool IsStructOrInterface,
    [property: JsonRequired] bool IsNested,
    ImmutableArray<string> BaseTypeNames,
    ImmutableArray<FocusedEvidenceAttributeUsageFact> Attributes,
    ImmutableArray<FocusedEvidenceDeclaredMethodFact> Methods);
