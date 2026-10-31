using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FocusedEvidenceResultText
{
    internal static string Format(FocusedEvidenceRunResult evidence, Func<string, string> trim)
    {
        var checks = evidence.Checks.Count == 0
            ? "checks: none"
            : "checks: " + string.Join("; ", evidence.Checks.Select(check => FormatCheck(check, trim)));
        var outcome = FindingEvidenceOutcomeReasonJsonConverter.ToWireValue(
            evidence.OutcomeReason ?? FindingEvidenceOutcomeReason.Unknown);
        return $"request='{trim(evidence.Request)}'; accepted={evidence.Accepted}; passed={evidence.Passed}; outcome={outcome}; " +
            $"summary={trim(evidence.Summary)}; {checks}";
    }

    private static string FormatCheck(AcceptanceCheckResult check, Func<string, string> trim)
    {
        var receipt = string.IsNullOrWhiteSpace(check.ArtifactsPath)
            ? "receipt=none"
            : $"receipt={check.ArtifactsPath}";
        var summary = string.IsNullOrWhiteSpace(check.ResultSummary)
            ? string.Empty
            : $"; summary={trim(check.ResultSummary)}";
        var executed = check.ExecutedTestCount is null
            ? string.Empty
            : $" executed={check.ExecutedTestCount}";
        return $"{check.Name} passed={check.Passed} exit={check.ExitCode}{executed} {receipt}{summary}";
    }
}
