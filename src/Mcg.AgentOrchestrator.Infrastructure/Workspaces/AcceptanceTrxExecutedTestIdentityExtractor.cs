using System.Xml.Linq;
using FocusedEvidenceIdentityExtraction = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceIdentityExtraction;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceTrxExecutedTestIdentityExtractor
{
    internal static FocusedEvidenceIdentityExtraction Extract(IEnumerable<string>? trxPaths)
    {
        if (trxPaths is null)
        {
            return new FocusedEvidenceIdentityExtraction([], []);
        }

        var identities = new List<string>();
        var unreadableReceiptPaths = new List<string>();
        foreach (var trxPath in trxPaths.Where(File.Exists))
        {
            try
            {
                var document = XDocument.Load(trxPath, LoadOptions.None);
                var definitionsByTestId = document
                    .Descendants()
                    .Where(element =>
                        element.Name.LocalName.Equals("UnitTest", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
                    .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                identities.AddRange(document
                    .Descendants()
                    .Where(element => element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal) &&
                        !string.Equals(element.Attribute("outcome")?.Value?.Trim(), "NotExecuted", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(element.Attribute("outcome")?.Value?.Trim(), "Skipped", StringComparison.OrdinalIgnoreCase))
                    .Select(result =>
                    {
                        definitionsByTestId.TryGetValue(
                            result.Attribute("testId")?.Value ?? string.Empty,
                            out var definition);
                        return AcceptanceTrxTestIdentityResolver.Resolve(result, definition) ??
                            result.Attribute("testId")?.Value?.Trim() ??
                            "unknown test";
                    }));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                unreadableReceiptPaths.Add(trxPath);
            }
        }

        return new FocusedEvidenceIdentityExtraction(
            identities
                .Where(identity => !string.IsNullOrWhiteSpace(identity))
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            unreadableReceiptPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }
}
