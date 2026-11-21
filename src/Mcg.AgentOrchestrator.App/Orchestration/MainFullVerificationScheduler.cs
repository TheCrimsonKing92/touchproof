namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MainFullVerificationScheduler
{
    internal enum Decision { NotDue, Deferred, Admit }

    internal static Decision Decide(string? commit, bool alreadyRequested, DateTimeOffset? lastAdmission,
        DateTimeOffset now, TimeSpan interval, bool outstanding, int waitingGoalGates, bool capacityAdmitted)
    {
        if (string.IsNullOrWhiteSpace(commit) || alreadyRequested || outstanding ||
            (lastAdmission is { } previous && now - previous < interval))
            return Decision.NotDue;

        return waitingGoalGates > 0 || !capacityAdmitted ? Decision.Deferred : Decision.Admit;
    }
}
