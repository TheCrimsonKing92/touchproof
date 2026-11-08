namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class LaunchLockSiteFormatter
{
    internal static IEnumerable<string> FormatBySite(
        IReadOnlyList<LaunchLockClassSnapshot> classes, IReadOnlyList<LaunchLockSiteSnapshot> sites, int processId)
    {
        var byClass = sites.ToLookup(row => row.Key, StringComparer.Ordinal);
        foreach (var reported in LaunchLockTelemetry.ReportedClasses(classes))
        foreach (var row in byClass[reported.Key].OrderByDescending(row => row.Launches)
                     .ThenBy(row => row.Site.Site, StringComparer.Ordinal)
                     .ThenBy(row => row.Site.Git, StringComparer.Ordinal).Take(10))
            yield return FormattableString.Invariant(
                $"LAUNCH_LOCK_BY_SITE pid={processId} class={LaunchLockTelemetry.FormatKey(row.Key)} site={Token(row.Site.Site, "Unknown.Unknown")} git={Token(row.Site.Git, "-")} launches={row.Launches}");
    }

    private static string Token(string value, string empty) => value.Length == 0 ? empty :
        new string(value.Select(character => char.IsWhiteSpace(character) ? '_' : character).ToArray());
}
