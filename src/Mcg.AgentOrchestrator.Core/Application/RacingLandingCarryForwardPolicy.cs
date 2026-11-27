namespace Mcg.AgentOrchestrator.Core;

public enum RacingLandingCarryForwardAction { Hold }

public sealed record RacingLandingCarryForwardFacts(
    string Disposition, string OverlapKind, string LandedPath, string VerifiedPath, string UnknownReason, string Receipt)
{
    public IReadOnlyList<PolicyDecisionFact> ToRecordedFacts() => Array.AsReadOnly<PolicyDecisionFact>(
    [
        new("disposition", Disposition), new("overlapKind", OverlapKind), new("landedPath", LandedPath),
        new("verifiedPath", VerifiedPath), new("unknownReason", UnknownReason), new("receipt", Receipt)
    ]);

    public static RacingLandingCarryForwardFacts FromRecordedFacts(IReadOnlyList<PolicyDecisionFact> facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        if (facts.Count != 6)
            throw new InvalidOperationException("Racing landing carry-forward replay requires exactly six named facts.");

        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var fact in facts)
            if (!values.TryAdd(fact.Name, fact.Value))
                throw new InvalidOperationException($"Duplicate racing landing carry-forward fact '{fact.Name}'.");
        string Read(string name) => values.TryGetValue(name, out var value) ? value
            : throw new InvalidOperationException($"Missing racing landing carry-forward fact '{name}'.");

        var restored = new RacingLandingCarryForwardFacts(Read("disposition"), Read("overlapKind"),
            Read("landedPath"), Read("verifiedPath"), Read("unknownReason"), Read("receipt"));
        ValidateDisposition(restored.Disposition);
        return restored;
    }

    internal static void ValidateDisposition(string disposition)
    {
        if (disposition is not ("Overlap" or "Unknown"))
            throw new InvalidOperationException($"Invalid racing landing carry-forward disposition '{disposition}'.");
    }
}

public sealed record RacingLandingCarryForwardDecision(
    RacingLandingCarryForwardAction Action, int DiscriminatingRung, string DiscriminatingEvidence,
    string Reason, RacingLandingCarryForwardFacts Facts)
{
    public PolicyDecisionRecord ToRecord() => new(RacingLandingCarryForwardPolicy.StageName, Action.ToString(),
        DiscriminatingRung, DiscriminatingEvidence, Reason, Facts.ToRecordedFacts());
}

/// <summary>Explains why a racing landing requires revalidation of the prior acceptance verdict.</summary>
public static class RacingLandingCarryForwardPolicy
{
    public const string StageName = "racing-landing-carry-forward";

    public static RacingLandingCarryForwardDecision Evaluate(RacingLandingCarryForwardFacts facts)
    {
        ArgumentNullException.ThrowIfNull(facts);
        RacingLandingCarryForwardFacts.ValidateDisposition(facts.Disposition);
        if (facts.Disposition == "Overlap")
            return new(RacingLandingCarryForwardAction.Hold, 1, "main-advance-overlap",
                $"Main advanced with overlapping changes; revalidation required before landing. {facts.Receipt}", facts);

        return new(RacingLandingCarryForwardAction.Hold, 2, "main-advance-unknown",
            $"Acceptance candidate relationship could not be classified; revalidation required before landing. {facts.Receipt}", facts);
    }
}
