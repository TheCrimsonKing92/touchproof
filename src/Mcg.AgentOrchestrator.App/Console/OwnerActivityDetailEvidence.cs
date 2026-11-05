using System.Collections.Immutable;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Evidence belongs to one event. Narrative tokens and display vocabulary never rewrite it.
internal sealed record OwnerActivityDetailEvidence(string Original, ImmutableArray<OwnerConsoleField> Fields)
{
    internal static OwnerActivityDetailEvidence From(string original, params OwnerConsoleField[] fields) =>
        new(original, [.. fields]);

    public bool Equals(OwnerActivityDetailEvidence? other) => other is not null && Original == other.Original && Fields.SequenceEqual(other.Fields);
    public override int GetHashCode()
    {
        var hash = new HashCode(); hash.Add(Original);
        foreach (var field in Fields) hash.Add(field);
        return hash.ToHashCode();
    }
}
