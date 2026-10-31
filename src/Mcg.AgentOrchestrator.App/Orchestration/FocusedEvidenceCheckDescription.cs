using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FocusedEvidenceCheckDescription
{
    internal static string Format(AcceptanceCheckResult check, Func<string, string> trim)
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
