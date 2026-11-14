namespace Mcg.AgentOrchestrator.Core;

/// <summary>A verification activity relevant to a unit, with the evidence that supports it.</summary>
public sealed record ProjectVerificationKind
{
    public string UnitId { get; }
    public UnitVerificationKind Kind { get; }
    public ProjectFact<string> Evidence { get; }

    public ProjectVerificationKind(string unitId, UnitVerificationKind kind, ProjectFact<string> evidence)
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
