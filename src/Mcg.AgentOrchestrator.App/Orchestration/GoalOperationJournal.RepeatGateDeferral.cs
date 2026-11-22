using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Historical provenance only: the current obligation evidence still determines the hold.
internal sealed record RepeatGateDeferralReceipt(
    string AttemptId, string ReplayContextFingerprint, IReadOnlyList<string> ObligationIds);

internal static partial class GoalOperationJournal
{
    internal const string RepeatGateDeferralOperation = "conductor:operator-prelanding-repeat-gate";
    private const string RefusedCarryHoldOperation = "conductor:criterion-evidence-refused-carry-hold";

    internal sealed record RefusedCarryHistory(
        string BranchSha, string? MainSha, IReadOnlyList<AcceptanceCriterionEvidence.RefusedCarryBinding> Bindings,
        string Diagnostic, string? AttemptId)
    {
        // The shared landing path, including grouped landing, records a member revision without a solo gate pair.
        [JsonIgnore] public bool RequiresLandingProof => MainSha is null && AttemptId is null;
        [JsonIgnore] public bool IsLegacy { get; init; }
    }

    [GeneratedRegex(@"\Aobligations=(?<ids>[^;]+); candidate=(?<branch>[0-9a-f]+); main=(?<main>[0-9a-f]+); Acceptance passed for \k<branch>, but obligation '(?<id>[^']+)' is bound to (?<bound>[0-9a-f]+)\.", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyRefusedCarryDetail();

    internal static void RecordRefusedCarryHold(string? executionDirectory, Goal goal,
        string branchSha, string? mainSha, IReadOnlyList<CriterionEvidenceObligation> bindings, string diagnostic,
        string? attemptId = null)
    {
        if (string.IsNullOrWhiteSpace(executionDirectory)) return;
        var history = new RefusedCarryHistory(branchSha, mainSha,
            bindings.OrderBy(item => item.Id, StringComparer.Ordinal)
                .Select(AcceptanceCriterionEvidence.BindingOf).ToArray(), diagnostic, attemptId);
        var instance = RefusedCarryPairKey(branchSha, mainSha);
        var detail = JsonSerializer.Serialize(history, JsonOptions);
        if (ReadActive(executionDirectory, goal.Id).Entries.Any(entry =>
                entry.Operation == RefusedCarryHoldOperation && entry.OperationInstanceId == instance &&
                entry.Detail == detail))
            return;
        Append(executionDirectory, goal.Id, Key(goal.Id, RefusedCarryHoldOperation),
            RefusedCarryHoldOperation, GoalOperationStatus.Completed, detail, branchSha, mainSha, null,
            operationInstanceId: instance);
    }

    internal static IEnumerable<RefusedCarryHistory> ReadRefusedCarryHolds(string executionDirectory, GoalId goalId)
    {
        foreach (var entry in ReadActive(executionDirectory, goalId).Entries.Reverse())
        {
            if (entry.Operation != RefusedCarryHoldOperation || entry.Status != GoalOperationStatus.Completed ||
                entry.Detail is null)
                continue;

            var history = entry.BranchHeadSha is null
                ? ReadLegacyRefusedCarryHold(entry, entry.Detail)
                : JsonSerializer.Deserialize<RefusedCarryHistory>(entry.Detail, JsonOptions);
            if (history is not null)
                yield return history;
        }
    }

    private static RefusedCarryHistory? ReadLegacyRefusedCarryHold(GoalOperationJournalEntry entry, string detail)
    {
        var match = LegacyRefusedCarryDetail().Match(detail);
        if (!match.Success) return null;
        var branch = match.Groups["branch"].Value;
        var main = match.Groups["main"].Value;
        if (entry.OperationInstanceId != RefusedCarryPairKey(branch, main) ||
            !string.Equals(entry.MainHeadSha, main, StringComparison.OrdinalIgnoreCase))
            return null;

        var describedId = match.Groups["id"].Value;
        var ids = match.Groups["ids"].Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (!ids.Contains(describedId, StringComparer.Ordinal)) return null;
        var bound = match.Groups["bound"].Value.ToLowerInvariant();
        var bindings = ids.Select(id => new AcceptanceCriterionEvidence.RefusedCarryBinding(
            id, id == describedId ? bound : null)).ToArray();
        return new RefusedCarryHistory(branch, main, bindings, detail, AttemptId: null) { IsLegacy = true };
    }

    internal static bool HasRefusedCarryHoldForPair(string executionDirectory, Goal goal,
        ConductorParallelAcceptanceAttempt attempt) =>
        ReadActive(executionDirectory, goal.Id).Entries.Any(entry =>
            entry.Operation == RefusedCarryHoldOperation && entry.Status == GoalOperationStatus.Completed &&
            entry.OperationInstanceId == RefusedCarryPairKey(attempt.BranchHeadSha, attempt.MainHeadSha) &&
            (entry.BranchHeadSha is null
                ? entry.At >= attempt.StartedAt
                : entry.Detail is not null &&
                  JsonSerializer.Deserialize<RefusedCarryHistory>(entry.Detail, JsonOptions)?.AttemptId == attempt.AttemptId));

    private static string RefusedCarryPairKey(string? branchSha, string? mainSha) =>
        $"{branchSha?.ToLowerInvariant()}:{mainSha?.ToLowerInvariant()}";

    internal static bool HasFullGateDeferral(string executionDirectory, Goal goal,
        ConductorParallelAcceptanceAttempt attempt) =>
        HasRepeatGateDeferral(executionDirectory, goal, attempt) ||
        HasRefusedCarryHoldForPair(executionDirectory, goal, attempt);

    internal static void RepeatGateDeferred(string executionDirectory, Goal goal,
        ConductorParallelAcceptanceAttempt attempt, IReadOnlyList<CriterionEvidenceObligation> blockers)
    {
        if (HasRepeatGateDeferral(executionDirectory, goal, attempt)) return;
        var receipt = new RepeatGateDeferralReceipt(attempt.AttemptId, attempt.ReplayContextFingerprint!,
            blockers.Select(item => item.Id).Order(StringComparer.Ordinal).ToArray());
        Append(executionDirectory, goal.Id, $"{Key(goal.Id, RepeatGateDeferralOperation)}:{attempt.AttemptId}",
            RepeatGateDeferralOperation, GoalOperationStatus.Completed, JsonSerializer.Serialize(receipt, JsonOptions),
            attempt.BranchHeadSha, attempt.MainHeadSha, null);
    }

    internal static bool HasRepeatGateDeferral(string executionDirectory, Goal goal,
        ConductorParallelAcceptanceAttempt attempt) =>
        Read(executionDirectory, goal.Id).Entries.Any(entry =>
            entry.Operation == RepeatGateDeferralOperation && entry.Status == GoalOperationStatus.Completed &&
            entry.HasCandidate(attempt.BranchHeadSha, attempt.MainHeadSha) && entry.Detail is not null &&
            JsonSerializer.Deserialize<RepeatGateDeferralReceipt>(entry.Detail, JsonOptions) is { ObligationIds.Count: > 0 } receipt &&
            receipt.AttemptId == attempt.AttemptId && receipt.ReplayContextFingerprint == attempt.ReplayContextFingerprint);
}
