namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class CarryForwardRefusalText
{
    internal static string Format(string reason, string boundHead, string evaluatedHead) =>
        reason.StartsWith("integration-merge-not-clean;", StringComparison.Ordinal)
            ? $"reason={reason}; head={evaluatedHead}; bound={boundHead}"
            : $"reason={reason}; head={boundHead}";
}
