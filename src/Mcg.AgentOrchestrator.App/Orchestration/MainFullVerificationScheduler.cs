namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MainFullVerificationScheduler
{
    internal enum Decision { NotDue, Deferred, Admit }

    internal static Decision Decide(string? commit, bool alreadyRequested, DateTimeOffset? lastAdmission,
        DateTimeOffset now, TimeSpan interval, bool outstanding, int waitingGoalGates, bool capacityAdmitted,
        bool shortRetryDue)
    {
        if (string.IsNullOrWhiteSpace(commit) || outstanding ||
            (!shortRetryDue && (alreadyRequested || !IntervalElapsed(lastAdmission, now, interval))))
            return Decision.NotDue;

        return waitingGoalGates > 0 || !capacityAdmitted ? Decision.Deferred : Decision.Admit;
    }

    internal static bool IntervalElapsed(DateTimeOffset? lastAdmission, DateTimeOffset now, TimeSpan interval) =>
        lastAdmission is null || now - lastAdmission.Value >= interval;
}
