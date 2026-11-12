using System.Globalization;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Eligibility evidence only: the existing rerun machinery owns selection and the final verdict.
internal static class AcceptanceIncompleteShortfallExplanation
{
    internal static bool IsExplained(
        AcceptanceShardCompletionDecision? decision,
        IReadOnlyList<string> failingIdentities,
        IReadOnlyList<string>? resultPaths)
    {
        if (decision?.DiscoveredTestCount is not { } discovered ||
            decision.ExecutedTestCount is not { } executed ||
            decision.NotExecutedTestCount is not { } notExecuted ||
            discovered < 0 || executed < 0 || notExecuted < 0 ||
            resultPaths is not { Count: > 0 })
            return false;

        var shortfall = (long)discovered - executed - notExecuted;
        if (shortfall < 0)
            return false;

        var named = failingIdentities.ToHashSet(StringComparer.Ordinal);
        var excluded = new HashSet<string>(StringComparer.Ordinal);
        var included = new HashSet<string>(StringComparer.Ordinal);
        long receiptDiscovered = 0, receiptExecuted = 0, receiptNotExecuted = 0;
        try
        {
            foreach (var path in resultPaths)
            {
                if (!File.Exists(path))
                    return false;
                var document = XDocument.Load(path, LoadOptions.None);
                var counters = document.Descendants().FirstOrDefault(element => element.Name.LocalName == "Counters");
                var results = document.Descendants().Where(element => element.Name.LocalName == "UnitTestResult").ToArray();
                // Match completion adjudication's fallbacks, but reject malformed declared counters.
                foreach (var name in new[] { "total", "executed", "notExecuted", "passed", "failed", "timeout" })
                    if (counters?.Attribute(name) is not null && ReadCounter(counters, name) is null)
                        return false;
                var fileExecuted = ReadCounter(counters, "executed");
                receiptDiscovered += ReadCounter(counters, "total") ??
                    document.Descendants().Count(element => element.Name.LocalName == "UnitTest");
                receiptExecuted += fileExecuted ?? results.Length;
                receiptNotExecuted += ReadCounter(counters, "notExecuted") ?? 0;
                var timeoutRows = results.Count(result => string.Equals(
                    result.Attribute("outcome")?.Value, "Timeout", StringComparison.OrdinalIgnoreCase));
                if (ReadCounter(counters, "timeout") is { } timeouts && timeouts != timeoutRows)
                    return false;

                var definitions = document.Descendants().Where(element => element.Name.LocalName == "UnitTest" &&
                        !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
                    .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var result in results)
                {
                    definitions.TryGetValue(result.Attribute("testId")?.Value ?? string.Empty, out var definition);
                    var identity = AcceptanceTrxTestIdentityResolver.Resolve(result, definition);
                    if (identity is null || !named.Contains(identity))
                        continue;
                    var outcome = result.Attribute("outcome")?.Value;
                    // Timeout is separate from an explicit executed counter. With no counter,
                    // the row-count fallback already includes it. Failed normally is executed;
                    // only counters explicitly accounting for execution as passed prove otherwise.
                    var outsideExecution = fileExecuted is not null &&
                        (string.Equals(outcome, "Timeout", StringComparison.OrdinalIgnoreCase) ||
                         (string.Equals(outcome, "Failed", StringComparison.OrdinalIgnoreCase) &&
                          ReadCounter(counters, "failed") is > 0 &&
                          ReadCounter(counters, "passed") == fileExecuted));
                    (outsideExecution ? excluded : included).Add(identity);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or
                                   ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            return false;
        }

        return receiptDiscovered == discovered && receiptExecuted == executed && receiptNotExecuted == notExecuted &&
            !excluded.Overlaps(included) && excluded.Count == shortfall;
    }

    private static int? ReadCounter(XElement? counters, string name) =>
        int.TryParse(counters?.Attribute(name)?.Value, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            ? value : null;
}
