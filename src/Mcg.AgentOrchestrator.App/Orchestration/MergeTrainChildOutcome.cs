using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MergeTrainChildOutcome
{
    internal const string InfrastructureFailure = "infrastructure-failure";
    internal const string Invalidated = "invalidated";
    internal const string NoReceipt = "no-receipt";

    internal static ConductorGroupedGateOutcome From(ConductorMergeTrainRunResult result)
    {
        if (result.RecordedReceipt is { } receipt)
        {
            var verdict = receipt.Outcome switch
            {
                MergeTrainGateOutcome.Passed => "passed",
                MergeTrainGateOutcome.Failed => "failed",
                MergeTrainGateOutcome.InfrastructureFailure => InfrastructureFailure,
                MergeTrainGateOutcome.Invalidated => Invalidated,
                _ => throw new ArgumentOutOfRangeException(nameof(result), receipt.Outcome, "Unknown train gate outcome.")
            };
            return new(verdict, receipt.ReceiptId) { Detail = result.Detail };
        }

        if (result.Detail.StartsWith($"outcome={CohortStableSlotAcquisitionRounds.DeferredVerdict} ",
            StringComparison.Ordinal))
            return CohortStableSlotAcquisitionRounds.DeferredGroupedOutcome;

        return new(NoReceipt, null) { Detail = result.Detail };
    }
}
