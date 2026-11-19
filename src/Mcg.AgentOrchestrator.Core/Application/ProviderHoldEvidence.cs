using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>The provider's confirmed refusal, independent of the worker completion verdict.</summary>
internal sealed record ProviderHoldEvidence(ProviderFailureKind Kind, string Line, DateTimeOffset? ResetAt)
{
    private static readonly ConditionalWeakTable<TaskVerificationRecord, ProviderHoldEvidence> Cache = new();
    private static readonly ProviderHoldEvidence Indeterminate = new(ProviderFailureKind.Unknown, "", null);
    private static readonly Regex UsageWindow = new(
        @"\b(?:usage|weekly|session|daily) limit\b|\bquota exceeded\b|\binsufficient_quota\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    internal static ProviderHoldEvidence Read(TaskVerificationRecord verification) =>
        verification.ExitCode == 0 || (verification.EvidenceSource == VerificationEvidenceSource.Manual &&
            verification.ProviderFailureKind is not (ProviderFailureKind.BudgetExhausted or ProviderFailureKind.AuthenticationRequired))
            ? Indeterminate : Cache.GetValue(verification, ReadVerification);

    private static ProviderHoldEvidence ReadVerification(TaskVerificationRecord verification)
    {
        if (verification.Succeeded || !string.IsNullOrWhiteSpace(verification.OrchestratorFailureReason) ||
            DirtyDispatchEvidence.IsDirtyDispatchGuardFailure(verification))
            return Indeterminate;
        var outcome = new WorkerProviderOutcome(verification.ExitCode,
            string.Join("\n", DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, true, false)),
            string.Join("\n", DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, false, true)));
        var evidence = Read(outcome, verification.CompletedAt);
        if (DispatchCompletionEvidence.HasWorkerEvidenceThatOutranksSubscriptionLimit(
                verification, verification.WorkerResultPresent, verification.HasCommittedChanges))
            return Indeterminate;

        // Older dispatch receipts may retain the positively classified kind without the full diagnostic.
        return evidence.Kind != ProviderFailureKind.Unknown ? evidence :
            verification.ProviderFailureKind is ProviderFailureKind.BudgetExhausted or ProviderFailureKind.AuthenticationRequired
                ? new(verification.ProviderFailureKind, "", null) : Indeterminate;
    }

    internal static ProviderHoldEvidence Read(WorkerProviderOutcome outcome, DateTimeOffset completedAt)
    {
        if (outcome.ExitCode == 0) return Indeterminate;
        if (ProviderAuthenticationEvidence.TryGetRequiredLine(outcome, out var auth))
            return new(ProviderFailureKind.AuthenticationRequired, auth, null);

        var stderr = ProviderAuthenticationEvidence.TrustedLines(outcome.StandardError).ToArray();
        var stdout = outcome.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        if (!ProviderLimitEvidenceParser.TryGetBudgetExhaustionEvidenceLine(stderr, out var quota) &&
            !ProviderLimitEvidenceParser.TryGetCliCreditExhaustionLine(outcome.ExitCode, stdout, out quota))
        {
            quota = ProviderLimitEvidenceParser.GetCliRefusalLines(outcome.ExitCode, stdout)
                .FirstOrDefault(line => ProviderLimitEvidenceParser.GetCliRefusalModelName(line) is not null ||
                    UsageWindow.IsMatch(line)) ??
                stderr.FirstOrDefault(IsUsageWindowDiagnostic) ??
                (stdout.Length > 0 && stdout.All(IsUsageWindowDiagnostic) ? stdout[0] : null);
            if (quota is null) return Indeterminate;
        }

        var resetAt = ProviderBudgetHoldSchedules.TryParseProviderReset(quota, completedAt, out var reset) ? reset : (DateTimeOffset?)null;
        return new(ProviderFailureKind.BudgetExhausted, quota, resetAt);
    }

    internal string? ScopedModel(TaskDispatchRecord dispatch)
    {
        var named = ProviderLimitEvidenceParser.GetCliRefusalModelName(Line);
        if (named is null || string.IsNullOrWhiteSpace(dispatch.ModelName)) return null;
        var scope = new ProviderSubscriptionCooldown("", default, default) { ModelName = named };
        return scope.AppliesToModel(dispatch.ModelName) ? dispatch.ModelName : null;
    }

    internal bool Confirmed => Kind is ProviderFailureKind.BudgetExhausted or ProviderFailureKind.AuthenticationRequired;

    private static bool IsUsageWindowDiagnostic(string line) =>
        UsageWindow.IsMatch(line) && ProviderLimitEvidenceParser.IsProviderLimitEvidenceLine(line);
}
