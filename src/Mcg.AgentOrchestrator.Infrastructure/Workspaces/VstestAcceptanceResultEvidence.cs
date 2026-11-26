namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class VstestAcceptanceResultEvidence
{
    internal static AcceptanceCheckResult Attach(AcceptanceCheckResult check, bool isFocusedSelection)
    {
        if (!isFocusedSelection || check.CompletionDecision is not { Passed: false, TrxOutcome: "missing" } completion)
        {
            return GoalAcceptanceVerifierTestTelemetry.WithNormalizedTrxResult(check);
        }

        // A failed selection with no TRX has no file for focused evidence to retain.
        check = AcceptanceMissingToolClassifier.Attach(check with { TestResultPaths = [] });
        return !completion.TimedOut && check.FailureClassification is null
            ? check with { FailureClassification = AcceptanceFailureClassifications.FocusedSelectionApparatusFailure }
            : check;
    }
}
