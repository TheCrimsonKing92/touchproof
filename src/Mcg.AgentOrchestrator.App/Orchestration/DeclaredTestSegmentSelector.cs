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
            while (offset < selectorSegments.Length && string.Equals(
                selectorSegments[offset], identitySegments[start + offset], StringComparison.Ordinal))
                offset++;
            if (offset == selectorSegments.Length) return true;
        }

        return false;
    }
}
