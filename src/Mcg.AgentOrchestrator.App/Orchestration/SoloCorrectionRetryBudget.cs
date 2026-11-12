using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SoloCorrectionRetryBudget
{
    internal static string? HoldReason(Goal goal, AttributedFailure failure, int cap)
    {
        var routes = goal.Timeline.Where(evt => evt.Kind == ProgressKind.TaskRetried &&
            evt.Message.StartsWith("ATTRIBUTED_FAILURE_ROUTE source=solo ", StringComparison.Ordinal))
            // An operator re-gate resets automatic retries without deleting timeline evidence.
            .TakeLast(goal.AutomaticAcceptanceRetryCount).ToArray();
        // Unlike the dispatch signature, correction identity survives candidate changes.
        // Compare the whole canonical line: theory identities can themselves contain commas.
        var tests = "failing_tests=" + string.Join(',', failure.FailingTests.Select(test => test.Trim())
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));
        if (routes.Count(evt => evt.Message.Split('\n').Any(line => line.TrimEnd('\r') == tests)) >= cap)
            return SoloHoldReasons.RetryCap;
        return routes.LongLength >= 2L * cap ? SoloHoldReasons.CorrectionCeiling : null;
    }
}
