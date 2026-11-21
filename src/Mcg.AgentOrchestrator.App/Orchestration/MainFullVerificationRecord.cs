namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record MainFullVerificationRecord(
    string Commit, DateTimeOffset AdmittedAt, int IntervalSeconds, DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null, double? DurationSeconds = null, MainFullVerificationResult? Result = null);

internal sealed record MainFullVerificationResult(
    string Outcome, string? Detail = null, IReadOnlyList<MainFullVerificationFailure>? Failures = null);

internal sealed record MainFullVerificationFailure(string Test, string? Landing = null, string? Goal = null);
