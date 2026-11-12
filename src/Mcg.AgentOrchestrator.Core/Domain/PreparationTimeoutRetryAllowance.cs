namespace Mcg.AgentOrchestrator.Core;

internal static class PreparationTimeoutRetryAllowance
{
    internal static bool IsAvailable(TaskSpec task)
    {
        var history = task.VerificationHistory;
        if (history.Count == 0 || SandboxPreparationFailure.Read(history[^1]) is not
            { NativeCommandTimedOut: true }) return false;
        return FailureCount(task) == 1;
    }

    internal static bool IsExhausted(TaskSpec task) =>
        SandboxPreparationFailure.ReadLatest(task) is not null && FailureCount(task) >= 2;

    private static int FailureCount(TaskSpec task)
    {
        var count = 0;
        foreach (var verification in task.VerificationHistory.Reverse())
        {
            // The admission start stamp can describe the preparation host. A child
            // identity or worker result proves the worker actually got past preparation.
            if (verification.ChildProcessId is > 0 || verification.WorkerResultPresent) break;
            if (SandboxPreparationFailure.Read(verification) is not null && ++count == 2) break;
        }
        return count;
    }
}
