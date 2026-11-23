using static Mcg.AgentOrchestrator.Core.CliRefusalSignals;
using static Mcg.AgentOrchestrator.Core.DispatchFailureClassifier;
using static Mcg.AgentOrchestrator.Core.DirtyDispatchEvidence;

namespace Mcg.AgentOrchestrator.Core;

public static class SubscriptionLimitEvidence
{
    public static bool IsRecoverableSubscriptionLimitFailure(TaskVerificationRecord verification)
    {
        if (verification.Succeeded)
        {
            return false;
        }

        if (IsDirtyDispatchGuardFailure(verification))
        {
            return false;
        }

        if (DispatchCompletionEvidence.HasWorkerEvidenceThatOutranksSubscriptionLimit(
            verification,
            verification.WorkerResultPresent,
            verification.HasCommittedChanges))
        {
            return false;
        }

        if (ProviderFailureSignals.TryGetProviderAuthenticationLine(verification, out _) ||
            ProviderFailureSignals.TryGetProviderConnectivityLine(verification, out _))
        {
            return false;
        }

        return TryGetRecoverableSubscriptionLimitLine(verification, out _);
    }

    public static bool HasRecoverableSubscriptionLimitEvidence(TaskVerificationRecord verification) =>
        !IsDirtyDispatchGuardFailure(verification) &&
        TryGetRecoverableSubscriptionLimitLine(verification, out _);

    public static bool HasRecoverableSubscriptionLimitHistory(TaskSpec task)
    {
        return task.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Pending &&
            task.LastVerification is null &&
            task.VerificationHistory.LastOrDefault() is { Succeeded: false } latest &&
            IsRecoverableSubscriptionLimitFailure(latest);
    }

    public static int CountRecoverableSubscriptionLimitFailures(TaskSpec task) =>
        CountRecoverableSubscriptionLimitFailures(task, includeProviderHolds: true);

    internal static int CountRecoverableSubscriptionLimitFailures(TaskSpec task, bool includeProviderHolds)
    {
        return task.VerificationHistory.Count(verification =>
            !verification.Succeeded &&
            (includeProviderHolds || !ProviderHoldRetry.IsHoldInterruption(verification)) &&
            IsRecoverableSubscriptionLimitFailure(verification));
    }

    internal static TaskDispatchRecord? FindDispatchForVerification(
        TaskSpec task,
        TaskVerificationRecord verification)
    {
        if (verification.DispatchAttemptAt is { } dispatchAttemptAt)
        {
            var exact = task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt == dispatchAttemptAt);
            if (exact is not null)
            {
                return exact;
            }
        }

        if (verification.DispatchStartedAt is { } dispatchStartedAt)
        {
            var exact = task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt == dispatchStartedAt);
            if (exact is not null)
            {
                return exact;
            }
        }

        return task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt <= verification.CompletedAt);
    }

    internal static ProviderBindingKey BuildProviderBindingKey(string providerName, string? credentialBinding)
    {
        var provider = string.IsNullOrWhiteSpace(providerName) ? "unknown" : providerName.Trim();
        if (string.IsNullOrWhiteSpace(credentialBinding))
        {
            return new ProviderBindingKey($"{provider.ToLowerInvariant()}::<provider-default>", "provider-only");
        }

        return new ProviderBindingKey(
            $"{provider.ToLowerInvariant()}::{credentialBinding.Trim().ToLowerInvariant()}",
            "provider+credential");
    }

    // Key identifies the provider and credential. Model is a separate component
    // because a credential can contain the delimiter used in display text.
    internal sealed record ProviderBindingKey(string Key, string Scope, string? Model = null)
    {
        internal string RenderedKey => ProviderHoldBinding.Render(Key, Model);
    }

    internal static bool TryGetRecoverableSubscriptionLimitLine(TaskVerificationRecord verification, out string line)
    {
        if (ProviderLimitEvidenceParser.TryGetEvidenceLine(
                DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: true),
                out line) || TryGetCliRefusalLimitLine(verification, out line))
            return true;

        var hold = ProviderHoldEvidence.Read(verification);
        if (!hold.IsWeeklyUsageLimitReached) return false;
        line = hold.Line;
        return true;
    }

    internal static bool TryGetCliAbsoluteResetRetryAfter(
        string line, DateTimeOffset completedAt, out DateTimeOffset retryAfter) =>
        TryGetAbsoluteResetRetryAfter(line, completedAt, out retryAfter);

    public static bool IsSubscriptionProviderCliDispatch(TaskSpec task) =>
        task.LastDispatch is { } dispatch && IsSubscriptionProviderCliDispatch(dispatch);

    internal static bool IsSubscriptionProviderCliDispatch(TaskDispatchRecord dispatch)
    {
        if (dispatch.WorkerProviderKind is
            ProviderKind.OpenAICodexCli or
            ProviderKind.AnthropicClaudeCli or
            ProviderKind.OpenAICodexLuna or
            ProviderKind.OpenAICodexOssCli or
            ProviderKind.OllamaQwenCodeCli or
            ProviderKind.XaiGrokCli)
        {
            return true;
        }

        return dispatch.WorkerName.Equals("codex-cli", StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("claude-cli", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(LunaLaneNames.NormalizeProfileName(dispatch.WorkerName), LunaLaneNames.LunaProfileName, StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("codex-oss-cli", StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("qwen-code-cli", StringComparison.OrdinalIgnoreCase) ||
            dispatch.WorkerName.Equals("grok-cli", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetCliRefusalLimitLine(TaskVerificationRecord verification, out string line)
    {
        line = GetCliRefusalSignalLines(verification)
            .FirstOrDefault(ProviderLimitEvidenceParser.IsCliRefusalLimitLine) ?? string.Empty;
        return line.Length > 0;
    }
}
