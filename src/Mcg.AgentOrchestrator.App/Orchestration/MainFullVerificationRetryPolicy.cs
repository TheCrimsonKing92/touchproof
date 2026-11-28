namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MainFullVerificationRetryPolicy
{
    internal const int MaxAttempts = 3;
    private static readonly TimeSpan ShortRetryDelay = TimeSpan.FromMinutes(3);

    // The caller looks up the completion by the latest admission's attempt identity.
    internal static bool IsDue(MainFullVerificationRecord? completed, DateTimeOffset now) =>
        completed is { Attempt: < MaxAttempts, CompletedAt: { } finished,
            Result: { Outcome: "incomplete", FromRunException: true } } &&
        now - finished >= ShortRetryDelay;
}
