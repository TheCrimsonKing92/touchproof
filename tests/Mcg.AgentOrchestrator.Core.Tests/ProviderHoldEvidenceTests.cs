using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: provider output is fixture text and timestamps are fixed.
public sealed class ProviderHoldEvidenceTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(1, "You've hit your weekly limit · resets Oct 7, 12pm (America/Chicago)", "", ProviderFailureKind.BudgetExhausted, "2026-10-07T17:00:00Z")]
    [InlineData(1, "You've hit your weekly limit · resets in 999999999h", "", ProviderFailureKind.BudgetExhausted, null)]
    [InlineData(1, "", "ERROR: weekly usage limit reached", ProviderFailureKind.BudgetExhausted, null)]
    [InlineData(1, "ERROR: You've hit your usage limit.", "", ProviderFailureKind.BudgetExhausted, null)]
    [InlineData(1, "", "API error (status 402 Payment Required): usage balance exhausted", ProviderFailureKind.BudgetExhausted, null)]
    [InlineData(1, "", "ERROR: 401 invalid credentials", ProviderFailureKind.AuthenticationRequired, null)]
    [InlineData(1, "", "ERROR: 429 retry after 30s", ProviderFailureKind.Unknown, null)]
    [InlineData(1, "", "ERROR: 403 Forbidden", ProviderFailureKind.Unknown, null)]
    [InlineData(1, "", "request timed out", ProviderFailureKind.Unknown, null)]
    [InlineData(1, "The CLI said: You've hit your weekly limit.", "", ProviderFailureKind.Unknown, null)]
    [InlineData(1, "", "```\nERROR: weekly usage limit reached\n```", ProviderFailureKind.Unknown, null)]
    [InlineData(1, "", "WORKER_RESULT:\nERROR: weekly usage limit reached\nEND_WORKER_RESULT", ProviderFailureKind.Unknown, null)]
    [InlineData(0, "", "ERROR: 401 invalid credentials", ProviderFailureKind.Unknown, null)]
    public void RequiresPositiveProviderEvidence(int exitCode, string stdout, string stderr, ProviderFailureKind kind, string? reset)
    {
        var evidence = ProviderHoldEvidence.Read(new WorkerProviderOutcome(exitCode, stdout, stderr), Now);

        Assert.Equal(kind, evidence.Kind);
        Assert.Equal(reset is null ? (DateTimeOffset?)null : DateTimeOffset.Parse(reset), evidence.ResetAt);
    }

    [Theory]
    [InlineData("sonnet", "claude-sonnet-4-6", "claude-sonnet-4-6")]
    [InlineData("weekly", "claude-sonnet-4-6", null)]
    public void ScopeRequiresTheDispatchedModel(string named, string dispatched, string? expected)
    {
        var evidence = ProviderHoldEvidence.Read(new WorkerProviderOutcome(1, $"You've hit your {named} limit", ""), Now);
        Assert.Equal(expected, evidence.ScopedModel(new("claude-cli", "fixture", ".", Now, ModelName: dispatched)));
    }

    [Theory]
    [InlineData("You've hit your weekly limit", "", false, true)]
    [InlineData("", "ERROR: 401 invalid credentials", false, true)]
    [InlineData("", "process timed out", true, true)]
    [InlineData("", "process timed out", false, false)]
    [InlineData("", "ERROR: try again later due to capacity", false, false)]
    public void RetryAdmissionCountsWorkerAttemptsAndPreservesProviderRecovery(string stdout, string stderr,
        bool admittedCheck, bool expected)
    {
        var task = new TaskSpec(TaskId.New(), "Worker retry fixture", AgentRole.Tester);
        var dispatch = new TaskDispatchRecord("claude-cli", "fixture", ".", Now);
        var fingerprint = new RetryContextFingerprint(RetryContextFingerprint.CurrentSchemaVersion, "same-context");
        task.RecordDispatch(dispatch);
        var first = RetryAdmissionPolicy.Evaluate(task, fingerprint, PaidRouteClassification.Paid, RetryCause.Unknown, Now, Now);
        Assert.True(first.AllowsProcessStart);
        task.RecordRetryAdmission(first.Receipt with { WorkerStartedAt = Now });
        task.RecordVerification(new("fixture", ".", 1, stdout, stderr, Now.AddSeconds(1), DispatchStartedAt: Now,
            CompletionVerdictRule: admittedCheck ? TaskOutcomeRules.ProviderHoldCheckInconclusive.Token : null));
        task.RecordRetry(Now.AddSeconds(2), RetryCause.ProviderInterruption);

        var next = RetryAdmissionPolicy.Evaluate(task, fingerprint, PaidRouteClassification.Paid,
            RetryCause.ProviderInterruption, Now.AddMinutes(1), Now.AddMinutes(1));
        Assert.Equal(expected, next.AllowsProcessStart);
    }
}
