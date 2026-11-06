namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceCaptureLimitDiagnostics
{
    internal static void Emit(
        string? goalId,
        string? attemptPrefix,
        string? path,
        long capBytes,
        TextWriter writer)
    {
        // Do not derive a run token from a suppressed capture filename.
        var runId = Path.GetFileName(
            attemptPrefix ?? (path is null ? null : Path.GetFileNameWithoutExtension(path))) ?? "unknown";
        var captureReference = path is null ? "" : $" path={GoalAcceptanceVerifier.QuoteProgressToken(path)}";
        writer.WriteLine(
            $"ACCEPTANCE_CAPTURE_LIMIT_REACHED goal={GoalAcceptanceVerifier.FormatNullableToken(goalId, 8)} run={GoalAcceptanceVerifier.QuoteProgressToken(runId)}{captureReference} cap_bytes={capBytes}");
        writer.Flush();
    }
}
