namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SelectedExecutionBackstopMissClassifier
{
    internal static (bool IsMiss, IReadOnlyList<string> UnmatchedTests,
        IReadOnlyList<SelectedExecutionBackstopLanding> UnclassifiableLandings) Classify(
        IReadOnlyList<string> failingTests, IEnumerable<SelectedExecutionBackstopLanding> coveredLandings)
    {
        var landings = coveredLandings.ToArray();
        var skipped = landings.Where(landing => landing.Applied)
            .SelectMany(landing => landing.SkippedClasses ?? [])
            .Select(name => name.Replace('+', '.')).ToHashSet(StringComparer.Ordinal);
        var unmatched = new List<string>();
        var isMiss = false;
        foreach (var identity in failingTests)
        {
            var test = identity.Trim();
            var arguments = test.IndexOf('(');
            if (arguments >= 0) test = test[..arguments];
            var method = test.LastIndexOf('.');
            // Keep namespace and generic spelling: a lossy match cannot establish a miss.
            if (method > 0 && skipped.Contains(test[..method].Replace('+', '.'))) isMiss = true;
            else unmatched.Add(identity);
        }
        return (isMiss, unmatched, isMiss ? [] : landings.Where(landing => landing.RecordUnreadable).ToArray());
    }
}
