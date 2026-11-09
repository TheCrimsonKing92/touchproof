using FocusedEvidenceSelectionCoverage = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceSelectionCoverage;
using FocusedEvidenceFilterToken = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceFilterToken;
using FocusedEvidenceTokenKind = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.FocusedEvidenceTokenKind;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedEvidenceSelectionCoverageInspector
{
    internal static FocusedEvidenceSelectionCoverage Inspect(
        IReadOnlyList<IReadOnlyList<FocusedEvidenceFilterToken>> selections,
        IEnumerable<string>? trxPaths)
    {
        if (selections.Count == 0)
            return FocusedEvidenceSelectionCoverage.Empty;

        var paths = trxPaths?.ToArray();
        var extraction = GoalAcceptanceVerifierTestTelemetry.ExtractTrxExecutedTestIdentities(paths);
        var uncoveredTokens = selections
            .SelectMany(selection => selection.Where(token =>
                token.Kind is FocusedEvidenceTokenKind.Class or FocusedEvidenceTokenKind.Method))
            .DistinctBy(token => token.CanonicalToken, StringComparer.Ordinal)
            .Where(token => !extraction.Identities.Any(identity =>
                GoalAcceptanceVerifierTestTelemetry.IsFocusedEvidenceIdentityMatch(identity, token.Value)))
            .ToArray();
        return new FocusedEvidenceSelectionCoverage(
            uncoveredTokens.Select(token => token.CanonicalToken).ToArray(),
            extraction.UnreadableReceiptPaths,
            FocusedEvidenceAllSkippedSelectionClassifier.Classify(uncoveredTokens, paths));
    }
}
