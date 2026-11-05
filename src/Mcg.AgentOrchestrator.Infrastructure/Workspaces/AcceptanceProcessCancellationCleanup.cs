using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptanceProcessCancellationCleanup
{
    internal static async Task StopOwnedTreeAsync(
        RegisteredOwnedProcess process,
        OperationCanceledException exception,
        TimeSpan timeout)
    {
        exception.Data["AcceptanceCancellationProcessId"] = process.Id;
        try
        {
            var termination = await process.KillTreeAndWaitForExitAsync(timeout).ConfigureAwait(false);
            exception.Data["AcceptanceCancellationTreeExitConfirmed"] = termination.ExitConfirmed;
            if (!termination.ExitConfirmed)
                exception.Data["AcceptanceCancellationRemainingProcessIds"] = string.Join(",", termination.RemainingProcessIds);
        }
        catch (Exception cleanupException)
        {
            // Preserve caller cancellation; report only the failure type, never command inputs.
            exception.Data["AcceptanceCancellationTreeExitConfirmed"] = false;
            exception.Data["AcceptanceCancellationCleanupFailure"] = cleanupException.GetType().Name;
        }
    }
}
