using System.Xml.Linq;
using FocusedEvidenceFilterToken = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceAllSkippedSelectionClassifier
{
    internal static IReadOnlyList<FocusedEvidenceAllSkippedSelection> Classify(
        IReadOnlyList<FocusedEvidenceFilterToken> uncoveredTokens, IEnumerable<string>? trxPaths)
    {
        if (uncoveredTokens.Count == 0 || trxPaths is null)
            return [];

        var results = new List<(string Identity, string? Outcome)>();
        foreach (var path in trxPaths)
        {
            // An absent or unreadable receipt cannot prove that every matching row was skipped.
            if (!File.Exists(path))
                return [];
            try
            {
                var document = XDocument.Load(path, LoadOptions.None);
                var definitionsByTestId = document.Descendants()
                    .Where(element => element.Name.LocalName.Equals("UnitTest", StringComparison.Ordinal) &&
                        !string.IsNullOrWhiteSpace(element.Attribute("id")?.Value))
                    .GroupBy(element => element.Attribute("id")!.Value, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
                foreach (var result in document.Descendants().Where(element =>
                    element.Name.LocalName.Equals("UnitTestResult", StringComparison.Ordinal)))
                {
                    definitionsByTestId.TryGetValue(result.Attribute("testId")?.Value ?? string.Empty, out var definition);
                    var identity = AcceptanceTrxTestIdentityResolver.Resolve(result, definition) ??
                        result.Attribute("testId")?.Value?.Trim() ?? "unknown test";
                    results.Add((identity, result.Attribute("outcome")?.Value?.Trim()));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return [];
            }
        }

        return uncoveredTokens.Where(token =>
        {
            var matching = results.Where(result =>
                GoalAcceptanceVerifierTestTelemetry.IsFocusedEvidenceIdentityMatch(result.Identity, token.Value)).ToArray();
            return matching.Length > 0 && matching.All(result =>
                string.Equals(result.Outcome, "NotExecuted", StringComparison.OrdinalIgnoreCase));
        }).Select(token => new FocusedEvidenceAllSkippedSelection(
            token.CanonicalToken, [FocusedEvidenceAllSkippedSelection.Reason])).ToArray();
    }
}
