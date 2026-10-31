namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class DeclaredTestSegmentSelector
{
    internal static bool Matches(string selector, string identity)
    {
        if (selector.Length == 0) return false;

        var selectorSegments = selector.Split("::");
        var identitySegments = identity.Split("::");
        for (var start = 0; start <= identitySegments.Length - selectorSegments.Length; start++)
        {
            var offset = 0;
            while (offset < selectorSegments.Length && MatchesSegment(
                selectorSegments[offset], identitySegments[start + offset],
                offset == selectorSegments.Length - 1))
                offset++;
            if (offset == selectorSegments.Length) return true;
        }

        return false;
    }

    private static bool MatchesSegment(string selector, string identity, bool isLast) =>
        string.Equals(selector, identity, StringComparison.Ordinal) ||
        isLast && selector.Length > 0 && identity.Length > selector.Length &&
        identity[selector.Length] == '.' && identity.StartsWith(selector, StringComparison.Ordinal);
}
