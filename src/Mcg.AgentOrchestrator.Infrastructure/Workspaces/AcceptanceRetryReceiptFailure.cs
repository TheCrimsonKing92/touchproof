namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceRetryReceiptFailure
{
    internal static AcceptanceCheckResult Build(
        AcceptanceCheckResult original,
        AcceptanceRetainedDiagnostic? retainedDiagnostic,
        string? originalInvocationId,
        Exception exception)
    {
        var detail =
            $"Within-attempt retry refused: {AcceptanceFailureClassifications.RetryReceiptFailure}; " +
            $"original_invocation_id={(string.IsNullOrWhiteSpace(originalInvocationId) ? "unavailable" : originalInvocationId)} " +
            $"retained_diagnostic={retainedDiagnostic?.Path ?? "unavailable"} " +
            $"(sha256={retainedDiagnostic?.Sha256 ?? "unavailable"}) " +
            $"exception={exception.GetType().Name}: {exception.Message}";
        return original with
        {
            Passed = false,
            OutputTail = string.IsNullOrWhiteSpace(original.OutputTail)
                ? detail
                : $"{original.OutputTail}{Environment.NewLine}{detail}",
            ResultSummary = AcceptanceDotnetBuildPhase.PrefixResultSummary(detail, original.ResultSummary),
            FailureClassification = AcceptanceFailureClassifications.RetryReceiptFailure,
            CompletionDecision = original.CompletionDecision is null
                ? null
                : original.CompletionDecision with
                {
                    PolicySignal = AcceptanceFailureClassifications.RetryReceiptFailure
                }
        };
    }

    internal static async Task<(AcceptanceCheckResult Result, bool RerunStarted)> RecordThenRerunAsync(
        Action recordReceipt,
        Func<Task<AcceptanceCheckResult>> rerun,
        AcceptanceCheckResult original,
        AcceptanceRetainedDiagnostic? retainedDiagnostic,
        string? originalInvocationId)
    {
        try
        {
            recordReceipt();
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            return (Build(original, retainedDiagnostic, originalInvocationId, ex), false);
        }

        return (await rerun().ConfigureAwait(false), true);
    }
}
