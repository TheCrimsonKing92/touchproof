namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class PlannerFixturePathMarker
{
    private const string Marker = "(fixture path)";

    internal static bool IsMarkedFixturePath(
        string section,
        int citationEnd,
        string citation,
        string candidate,
        bool hasExplicitDirectory)
    {
        return hasExplicitDirectory &&
            citation.IndexOfAny(['*', '?']) < 0 &&
            candidate.IndexOfAny(['*', '?']) < 0 &&
            section.AsSpan(citationEnd).TrimStart(" \t").StartsWith(Marker, StringComparison.Ordinal);
    }
}
