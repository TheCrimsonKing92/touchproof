using System.Globalization;
using System.Xml.Linq;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Unlike the slowdown snapshot this reader has no duration floor or top-N truncation.
internal static class AcceptanceClassDurationResultReader
{
    internal static IReadOnlyDictionary<string, double>? Read(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return null;
        try
        {
            var completion = AcceptanceShardCompletionAdjudicator.InspectTrxCompletion(paths);
            if (!completion.Passed || completion.FailedPredicate is not null ||
                completion.DiscoveredTestCount != completion.ExecutedTestCount + completion.NotExecutedTestCount) return null;
            var timing = LaneSlotTimingReader.Read(paths);
            var methods = new Dictionary<string, double>(StringComparer.Ordinal);
            foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var document = XDocument.Load(path);
                var definitions = document.Descendants().Where(node => node.Name.LocalName == "UnitTest")
                    .GroupBy(node => (string?)node.Attribute("id") ?? "", StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                var fileMethods = new Dictionary<string, double>(StringComparer.Ordinal);
                foreach (var row in document.Descendants().Where(node => node.Name.LocalName == "UnitTestResult"))
                {
                    var outcome = (string?)row.Attribute("outcome");
                    if (outcome is "NotExecuted" or "Skipped") continue;
                    if (!string.Equals(outcome, "Passed", StringComparison.OrdinalIgnoreCase)) return null;
                    if (!TimeSpan.TryParse((string?)row.Attribute("duration"), CultureInfo.InvariantCulture, out var duration) || duration < TimeSpan.Zero)
                        return null;
                    definitions.TryGetValue((string?)row.Attribute("testId") ?? "", out var definition);
                    var resolved = AcceptanceTrxTestIdentityResolver.Resolve(row, definition);
                    if (string.IsNullOrWhiteSpace(resolved)) return null;
                    var method = AcceptanceTrxTestIdentityResolver.NormalizeSelector(resolved);
                    if (method.LastIndexOf('.') < 1) return null;
                    fileMethods[method] = fileMethods.GetValueOrDefault(method) + duration.TotalMilliseconds;
                }
                foreach (var (method, milliseconds) in fileMethods)
                    methods[method] = Math.Max(methods.GetValueOrDefault(method),
                        LaneSlotTimingReader.Subtract(milliseconds, timing.WaitFor(path, method)));
            }
            return methods.GroupBy(pair => pair.Key[..pair.Key.LastIndexOf('.')], StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(pair => pair.Value) / 1000, StringComparer.Ordinal);
        }
        catch (Exception) { return null; }
    }

    internal static IReadOnlyList<string>? ReadNames(IReadOnlyList<string>? paths)
    {
        if (paths is null || paths.Count == 0) return null;
        try
        {
            return paths.Select(XDocument.Load).SelectMany(document => document.Descendants()
                    .Where(node => node.Name.LocalName == "TestMethod").Select(node => (string?)node.Attribute("className")))
                .OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        }
        catch (Exception) { return null; }
    }
}
