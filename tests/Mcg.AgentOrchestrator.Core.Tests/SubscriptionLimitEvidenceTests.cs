using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: in-memory records and fixed timestamps; no shared state or I/O.
public sealed class SubscriptionLimitEvidenceTests
{
    private const string WeeklyLimit =
        "You've hit your weekly limit \u00B7 resets Oct 7, 12pm (America/Chicago)";
    private static readonly DateTimeOffset CompletedAt =
        new(2026, 10, 5, 10, 12, 56, TimeSpan.Zero);

    [Fact]
    public void Failure_WeeklyLimit_IsRecoverableWithEvidence()
    {
        var verification = Verification(1, WeeklyLimit);

        // Baseline 6cf01770e: DispatchFailureClassifier.cs:341-373 returns true for both.
        Assert.True(SubscriptionLimitEvidence.IsRecoverableSubscriptionLimitFailure(verification));
        Assert.True(SubscriptionLimitEvidence.HasRecoverableSubscriptionLimitEvidence(verification));
    }

    [Theory]
    [InlineData(0, WeeklyLimit, true)]
    [InlineData(1, "error CS0103: The name 'missing' does not exist", false)]
    public void Failure_SuccessOrNoLimit_IsNotRecoverable(
        int exitCode, string stdout, bool hasEvidence)
    {
        var verification = Verification(exitCode, stdout);

        // Baseline 6cf01770e: :343-345 rejects success; :367 requires a limit line.
        // The evidence-only predicate at :370-373 deliberately ignores success.
        Assert.False(SubscriptionLimitEvidence.IsRecoverableSubscriptionLimitFailure(verification));
        Assert.Equal(hasEvidence,
            SubscriptionLimitEvidence.HasRecoverableSubscriptionLimitEvidence(verification));
    }

    [Fact]
    public void History_TwoFailedLimitRounds_CountsBoth()
    {
        var task = new TaskSpec(TaskId.New(), "Review subscription evidence", AgentRole.Reviewer);
        task.RestoreVerificationHistory(Verification(1, WeeklyLimit));
        task.RestoreVerificationHistory(Verification(1, WeeklyLimit) with
        {
            CompletedAt = CompletedAt.AddMinutes(1)
        });

        Assert.Equal(2, task.VerificationHistory.Count);
        // Baseline 6cf01770e: :1306-1319 sees pending history and counts both failures.
        Assert.True(SubscriptionLimitEvidence.HasRecoverableSubscriptionLimitHistory(task));
        Assert.Equal(2, SubscriptionLimitEvidence.CountRecoverableSubscriptionLimitFailures(task));

        task.RestoreVerificationHistory(Verification(1, "Compilation failed") with
        {
            CompletedAt = CompletedAt.AddMinutes(2)
        });
        task.RestoreVerificationHistory(Verification(0, WeeklyLimit) with
        {
            CompletedAt = CompletedAt.AddMinutes(3)
        });

        // Baseline 6cf01770e: unrelated failures and successful rounds do not count.
        Assert.Equal(4, task.VerificationHistory.Count);
        Assert.Equal(2, SubscriptionLimitEvidence.CountRecoverableSubscriptionLimitFailures(task));
        Assert.False(SubscriptionLimitEvidence.HasRecoverableSubscriptionLimitHistory(task));
    }

    [Fact]
    public void AbsoluteReset_ChicagoNoon_ReturnsUtcReset()
    {
        // Baseline 6cf01770e: :1357-1359 delegates to the unchanged absolute parser.
        // CliProviderRefusalClassificationTests.ClaudeLimitUsesAbsoluteReset pins this value.
        Assert.True(SubscriptionLimitEvidence.TryGetCliAbsoluteResetRetryAfter(
            WeeklyLimit, CompletedAt, out var retryAfter));
        Assert.Equal(new DateTimeOffset(2026, 10, 7, 17, 0, 0, TimeSpan.Zero), retryAfter);
    }

    [Fact]
    public void AbsoluteReset_MalformedDate_ReturnsFalse()
    {
        // Baseline 6cf01770e: the parser rejects the same input covered by
        // CliProviderRefusalClassificationTests.UnparseableResetKeepsLimitWithoutRetryTime.
        Assert.False(SubscriptionLimitEvidence.TryGetCliAbsoluteResetRetryAfter(
            "You've hit your weekly limit \u00B7 resets Feb 30, 12pm (America/Chicago)",
            CompletedAt, out _));
    }

    [Theory]
    [InlineData("openai-api", ProviderKind.Unknown, false)]
    [InlineData("hermes", ProviderKind.HermesAcp, false)]
    [InlineData("custom-profile", ProviderKind.AnthropicClaudeCli, true)]
    [InlineData("CODEX-CLI", ProviderKind.Unknown, true)]
    public void Dispatch_ProviderKindOrWorkerName_PreservesSubscriptionMembership(
        string workerName, ProviderKind providerKind, bool expected)
    {
        var dispatch = new TaskDispatchRecord(
            workerName, "opaque command", "C:\\repo", CompletedAt,
            WorkerProviderKind: providerKind);
        var task = new TaskSpec(TaskId.New(), "Review provider membership", AgentRole.Reviewer);
        task.RecordDispatch(dispatch);

        // Baseline 6cf01770e: :1361-1384 tests CLI kinds, then known profile names.
        Assert.Equal(expected, SubscriptionLimitEvidence.IsSubscriptionProviderCliDispatch(dispatch));
        Assert.Equal(expected, SubscriptionLimitEvidence.IsSubscriptionProviderCliDispatch(task));
    }

    private static TaskVerificationRecord Verification(int exitCode, string stdout) =>
        new("opaque command", "C:\\repo", exitCode, stdout, string.Empty, CompletedAt);
}
