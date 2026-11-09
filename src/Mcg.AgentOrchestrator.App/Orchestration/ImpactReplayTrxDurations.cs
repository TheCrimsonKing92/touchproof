using System.Globalization;
using System.Xml;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Read executed-result cost, never wall time or an equal share of lane time.
internal static class ImpactReplayTrxDurations
{
    internal static (IReadOnlyList<(string? ClassName, double? Seconds)> Results, bool Complete) Read(
        string path, IReadOnlyList<AcceptanceTestClassDescriptor> classes)
    {
        try
        {
            var document = XDocument.Load(path);
            var definitions = document.Descendants().Where(e => e.Name.LocalName == "UnitTest")
                .Where(e => e.Attribute("id") is not null)
                .GroupBy(e => e.Attribute("id")!.Value).ToDictionary(g => g.Key, g => g.First());
            var results = new List<(string? ClassName, double? Seconds)>();
            var executions = new HashSet<string>(StringComparer.Ordinal);
            foreach (var result in document.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
            {
                if (result.Attribute("executionId")?.Value is { Length: > 0 } execution && !executions.Add(execution)) continue;
                var outcome = result.Attribute("outcome")?.Value;
                if (string.Equals(outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(outcome, "Skipped", StringComparison.OrdinalIgnoreCase)) continue;
                definitions.TryGetValue(result.Attribute("testId")?.Value ?? "", out var definition);
                var method = definition?.Descendants().FirstOrDefault(e => e.Name.LocalName == "TestMethod");
                var className = method?.Attribute("className")?.Value;
                className = className is null ? ImpactReplayTrxEvidence.ResolveClass(result.Attribute("testName")?.Value, classes)
                    : AcceptanceTrxTestIdentityResolver.NormalizeSelector(className);
                double? seconds = TimeSpan.TryParse(result.Attribute("duration")?.Value, CultureInfo.InvariantCulture,
                    out var duration) && duration >= TimeSpan.Zero ? duration.TotalSeconds : null;
                results.Add((className, seconds));
            }
            return (results, results.Count > 0 && results.All(r => r.Seconds is not null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        { return ([], false); }
    }
}
