namespace Mcg.AgentOrchestrator.Infrastructure;

// Suggestions explain failed resolution; they never contribute obligation classes.
internal sealed record AcceptanceUnresolvedObligationSelector(
    string Selector, IReadOnlyList<string> ClosestCandidateClassNames)
{
    internal static AcceptanceUnresolvedObligationSelector Create(string selector,
        IReadOnlyList<AcceptanceTestClassDescriptor> candidates, bool wellFormed)
    {
        static string SimpleName(string name) => name[(Math.Max(name.LastIndexOf('.'), name.LastIndexOf('+')) + 1)..];

        var keys = new List<string> { wellFormed ? SimpleName(selector) : selector };
        var separator = selector.LastIndexOf('.');
        // Without a direct class identity, failed resolution can also mean a method-form selector.
        if (wellFormed && separator > selector.LastIndexOf('+') &&
            !candidates.Any(candidate => candidate.FullName == selector))
            keys.Add(SimpleName(selector[..separator]));
        var normalizedKeys = keys.Select(key => key.ToUpperInvariant()).ToArray();
        var closest = candidates.Select(candidate => candidate.FullName).Distinct(StringComparer.Ordinal)
            .Select(name => new
            {
                Name = name,
                Distance = normalizedKeys.Min(key => NormalizedDistance(key, SimpleName(name).ToUpperInvariant()))
            })
            .OrderBy(candidate => candidate.Distance).ThenBy(candidate => candidate.Name, StringComparer.Ordinal)
            .Take(3).Select(candidate => candidate.Name).ToArray();
        return new(selector, closest);
    }

    private static double NormalizedDistance(string left, string right)
    {
        var length = Math.Max(left.Length, right.Length);
        if (length == 0) return 0;
        if (left.Length < right.Length) (left, right) = (right, left);
        var previous = Enumerable.Range(0, right.Length + 1).ToArray();
        var current = new int[right.Length + 1];
        for (var row = 1; row <= left.Length; row++)
        {
            current[0] = row;
            for (var column = 1; column <= right.Length; column++)
                current[column] = Math.Min(Math.Min(current[column - 1] + 1, previous[column] + 1),
                    previous[column - 1] + (left[row - 1] == right[column - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return (double)previous[right.Length] / length;
    }
}
