using System.Runtime.CompilerServices;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Reads existing attempt evidence for narration only; never creates or extends a hold.
internal static class TrainEjectionReport
{
    // Narration belongs to this outcome instance; its original reason remains the kernel's fallback key.
    private static readonly ConditionalWeakTable<ConductorAdvanceOutcome.Held, string> OriginalHoldReasons = new();

    internal static (string Members, string Identity, string EjectedSuffix)? DescribeCompletion(
        string? stateDirectory, ConductorGroupedGateAttempt attempt, string? receiptId)
    {
        if (attempt.Kind != "train" || receiptId is null) return null;
        try
        {
            var store = Open(stateDirectory);
            var initial = store?.TryReadReceipt(attempt.IdentityValue);
            if (initial?.GateAttemptId is not { } attemptId) return null;
            var ejections = Ejections(store!, attemptId, attempt.Members.Select(member => member.GoalId));
            if (ejections.Count == 0) return null;
            var receipt = Receipts(store!, initial).SingleOrDefault(item => item.ReceiptId == receiptId);
            if (receipt is null) return null;
            return (string.Join('+', receipt.Identity.Members.Select(member => member.GoalId.Value)),
                receipt.Identity.Value,
                " ejected=" + string.Join(',', ejections.Select(item => $"{item.GoalId.Value}:{item.Reason}")));
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Reporting must not change reconciliation when retained evidence is unavailable.
            return null;
        }
    }

    internal static bool TryGetCohortMemberHold(ConductorDriver driver, GoalId goalId, out string detail)
    {
        if (!driver.TryGetCohortGateHold(goalId, out detail)) return false;
        if (driver.ExecutionDirectory is not { } executionDirectory ||
            Token(detail, "fingerprint=")?.StartsWith("train:", StringComparison.Ordinal) != true) return true;
        try
        {
            detail = DescribeHold(OrchestratorWorkspace.ForDirectory(executionDirectory).OrchestratorDirectory,
                goalId, detail);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            // Resolving the store is best effort too; the existing hold still applies.
        }
        return true;
    }

    // Refresh explanations on holds already chosen by admission; never retain the old train's fence.
    internal static bool RefreshHold(ConductorDriver driver, GoalId goalId,
        IDictionary<string, ParallelLandingOutcome> results)
    {
        if (!results.TryGetValue(goalId.Value, out var result)) return false;
        results[goalId.Value] = result with { Result = DescribeWaitingResult(driver, goalId, result.Result) };
        return true;
    }

    internal static ConductorAdvanceResult DescribeWaitingResult(
        ConductorDriver driver, GoalId goalId, ConductorAdvanceResult result)
    {
        if (result.Outcome is not ConductorAdvanceOutcome.Held held) return result;
        var originalReason = OriginalHoldReason(held);
        var reason = DescribeWaitingHold(driver, goalId, held, originalReason);
        if (reason == held.Reason) return result;
        var described = held with { Reason = reason };
        OriginalHoldReasons.Add(described, originalReason);
        return result with { Outcome = described };
    }

    internal static string OriginalHoldReason(ConductorAdvanceOutcome.Held held) =>
        OriginalHoldReasons.TryGetValue(held, out var reason) ? reason : held.Reason;

    internal static string DescribeWaitingHold(
        ConductorDriver driver, GoalId goalId, ConductorAdvanceOutcome.Held held, string reason)
    {
        if (!IsTrainWaitHold(held)) return reason;
        if (driver.ExecutionDirectory is not { } executionDirectory) return reason;
        try
        {
            var state = OrchestratorWorkspace.ForDirectory(executionDirectory).OrchestratorDirectory;
            var store = Open(state);
            if (store is null) return reason;
            var receipts = store.ReadReceiptsSince(DateTimeOffset.MinValue);
            var latest = receipts.LastOrDefault(receipt =>
                receipt.Identity.Members.Any(member => member.GoalId == goalId));
            if (latest?.GateAttemptId is not { } attemptId ||
                !store.ReadAttemptEjections(attemptId).Any(item => item.GoalId == goalId)) return reason;
            // A launched admission, including a reconciled one, retires the old explanation.
            // Merely considering or queuing a candidate leaves it intact.
            if (HasLaterAdmission(state, goalId, latest.CompletedAt)) return reason;
            var description = DescribeHold(state, goalId, $"fingerprint=train:{latest.Identity.Value}");
            return description.StartsWith("ejected from train ", StringComparison.Ordinal) ? description : reason;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return reason;
        }
    }

    // Landing waits are Verified; recovery can also resolve to Verified without being a train wait.
    private static bool IsTrainWaitHold(ConductorAdvanceOutcome.Held held) =>
        held.State == GoalLifecycleState.Verified &&
        held.Decision?.Stage != FailedGoalRecoveryDecisionRecords.StageName;

    internal static string DescribeHold(string? stateDirectory, GoalId goalId, string detail)
    {
        var fingerprint = Token(detail, "fingerprint=");
        if (fingerprint is null || !fingerprint.StartsWith("train:", StringComparison.Ordinal)) return detail;
        try
        {
            var store = Open(stateDirectory);
            var initial = store?.TryReadReceipt(fingerprint["train:".Length..]);
            if (initial?.GateAttemptId is not { } attemptId) return detail;
            var ejections = Ejections(store!, attemptId, initial.Identity.Members.Select(member => member.GoalId.Value));
            if (ejections.Count == 0) return detail;
            var ejectedIds = ejections.Select(item => item.GoalId).ToHashSet();
            var surviving = Receipts(store!, initial).LastOrDefault(receipt =>
                receipt.Identity.Value != initial.Identity.Value);
            var ejection = ejections.SingleOrDefault(item => item.GoalId == goalId);
            if (ejection is not null)
            {
                if (HasLaterAdmission(stateDirectory!, goalId, initial.CompletedAt)) return detail;
                return $"ejected from train {initial.Identity.Value} reason={ejection.Reason} " +
                    $"surviving-receipt={surviving?.ReceiptId ?? "pending"}";
            }

            var members = surviving?.Identity.Members ?? initial.Identity.Members
                .Where(member => !ejectedIds.Contains(member.GoalId)).ToArray();
            var owner = Token(detail, "owner=");
            if (owner is not null)
                detail = detail.Replace($"owner={owner}",
                    "owner=" + string.Join('+', members.Select(member => member.GoalId.Value).Order(StringComparer.Ordinal)),
                    StringComparison.Ordinal);
            return surviving is null ? detail : detail.Replace($"fingerprint={fingerprint}",
                $"fingerprint=train:{surviving.Identity.Value}", StringComparison.Ordinal);
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException))
        {
            return detail;
        }
    }

    private static bool HasLaterAdmission(string state, GoalId goalId, DateTimeOffset completedAt)
    {
        var grouped = Path.Combine(state, "grouped-gate-attempts");
        if (Directory.Exists(grouped) && Directory.EnumerateFiles(grouped, "*.attempt.json", SearchOption.AllDirectories)
            .Select(ConductorGroupedGateAttemptCoordinator.Read).Any(attempt =>
                (attempt.OwnerProcessStartedAt ?? attempt.StartedAt) > completedAt &&
                attempt.OwnerProcessId > 0 && attempt.Members.Any(member => member.GoalId == goalId.Value)))
            return true;
        var solo = Path.Combine(state, "acceptance-gate-attempts", goalId.Value);
        return Directory.Exists(solo) && Directory.EnumerateFiles(solo, "*.attempt.json")
            // Match the coordinator's camel-case metadata contract when reading solo admissions.
            .Select(path => JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(
                File.ReadAllText(path), JsonSerializerOptions.Web))
            .Any(attempt => attempt is { OwnerProcessId: > 0 } && attempt.GoalId == goalId.Value &&
                attempt.Kind == ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind &&
                (attempt.OwnerProcessStartedAt ?? attempt.StartedAt) > completedAt &&
                attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.LaunchFailed);
    }

    private static MergeTrainAcceptanceStore? Open(string? stateDirectory)
    {
        if (string.IsNullOrWhiteSpace(stateDirectory)) return null;
        var path = Path.Combine(stateDirectory, "merge-train-acceptance.db");
        return File.Exists(path) ? MergeTrainAcceptanceStore.OpenReadOnly(path) : null;
    }

    private static IReadOnlyList<MergeTrainReceipt> Receipts(MergeTrainAcceptanceStore store, MergeTrainReceipt initial) =>
        store.ReadReceiptsSince(initial.CompletedAt)
            .Where(receipt => (receipt.Recheck?.TrainAttemptId ?? receipt.GateAttemptId) == initial.GateAttemptId)
            .ToArray();

    private static IReadOnlyList<MergeTrainEjection> Ejections(
        MergeTrainAcceptanceStore store, string attemptId, IEnumerable<string> members)
    {
        var memberIds = members.ToHashSet(StringComparer.Ordinal);
        // GroupBy keeps first-ejection order; the latest recorded reason explains each member.
        return store.ReadAttemptEjections(attemptId).Where(item => memberIds.Contains(item.GoalId.Value))
            .GroupBy(item => item.GoalId).Select(group => group.Last()).ToArray();
    }

    private static string? Token(string detail, string prefix) =>
        detail.Split(' ').FirstOrDefault(token => token.StartsWith(prefix, StringComparison.Ordinal))?[prefix.Length..];
}
