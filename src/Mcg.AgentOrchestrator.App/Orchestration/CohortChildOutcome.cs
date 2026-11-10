using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class CohortChildOutcome
{
    internal const string NoReceipt = MergeTrainChildOutcome.NoReceipt;

    internal static string VerdictName(AcceptanceCohortGateOutcome outcome)
    {
        if (!Enum.IsDefined(outcome))
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown cohort gate outcome.");
        return JsonNamingPolicy.KebabCaseLower.ConvertName(outcome.ToString());
    }

    internal static bool IsPublishedVerdict(string? verdict) =>
        string.Equals(verdict, NoReceipt, StringComparison.Ordinal) ||
        Enum.GetValues<AcceptanceCohortGateOutcome>().Any(outcome =>
            string.Equals(verdict, VerdictName(outcome), StringComparison.Ordinal));

    internal static string Detail(AcceptanceCohortReceipt receipt) =>
        $"outcome={VerdictName(receipt.Outcome)} receipt={receipt.ReceiptId} attribution={receipt.Attribution} " +
        $"gateExit={receipt.GateExitCode?.ToString(CultureInfo.InvariantCulture) ?? "none"} " +
        $"infrastructureReason={receipt.InfrastructureReasonCode ?? "none"} " +
        $"invalidation={receipt.Invalidation?.Reason.ToString() ?? "none"} " +
        $"failedChecks={(receipt.FailedChecks.Count == 0 ? "none" : string.Join(',', receipt.FailedChecks))}" +
        (string.IsNullOrEmpty(receipt.InfrastructureDetail) ? "" : $" infrastructureDetail={receipt.InfrastructureDetail}");

    internal static ConductorGroupedGateOutcome From(
        AcceptanceCohortReceipt? receipt, bool slotsBusyDeferred, string identityValue)
    {
        if (receipt is not null)
            return new(VerdictName(receipt.Outcome), receipt.ReceiptId) { Detail = Detail(receipt) };

        if (slotsBusyDeferred) return CohortStableSlotAcquisitionRounds.DeferredGroupedOutcome;

        return new(NoReceipt, null) { Detail = $"outcome=no-receipt cohort={identityValue}" };
    }
}
