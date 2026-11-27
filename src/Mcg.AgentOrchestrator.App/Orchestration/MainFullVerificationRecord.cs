namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record MainFullVerificationRecord(
    string Commit, DateTimeOffset AdmittedAt, int IntervalSeconds, DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null, double? DurationSeconds = null, MainFullVerificationResult? Result = null,
    int Attempt = 1);

internal sealed record MainFullVerificationResult(
    string Outcome, string? Detail = null, IReadOnlyList<MainFullVerificationFailure>? Failures = null,
    bool FromRunException = false, IReadOnlyList<string>? Unconfirmed = null)
{
    internal const string PassedWithUnconfirmed = "passed-with-unconfirmed";
}

internal sealed record MainFullVerificationFailure(string Test, string? Landing = null, string? Goal = null);
