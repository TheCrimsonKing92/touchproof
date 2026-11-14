namespace Mcg.AgentOrchestrator.Core;

/// <summary>A unit's risk area supported by a declaration or an explicitly uncertain inference.</summary>
public sealed record ProjectRiskArea
{
    public string UnitId { get; }
    public ProjectRiskAreaKind Kind { get; }
    public ProjectFact<string> Evidence { get; }

    public ProjectRiskArea(string unitId, ProjectRiskAreaKind kind, ProjectFact<string> evidence)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unitId);
        ArgumentNullException.ThrowIfNull(evidence);
        if (!Enum.IsDefined(kind))
            throw new ArgumentOutOfRangeException(nameof(kind));
        UnitId = unitId;
        Kind = kind;
        Evidence = evidence;
    }
}
