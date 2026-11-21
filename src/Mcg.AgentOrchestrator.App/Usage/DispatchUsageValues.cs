using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Usage;

internal static class DispatchUsageValues
{
    internal static DispatchProviderUsage? RecordedUsage(TaskDispatchRecord dispatch) =>
        dispatch.ContextPackageReceipt is { } receipt
            ? new(receipt.InputTokens, receipt.CachedInputTokens, receipt.OutputTokens)
            : dispatch.ProviderUsage;

    internal static long? RecordedTurns(TaskDispatchRecord dispatch) =>
        ReportedCount(dispatch.ContextPackageReceipt is { } receipt ? receipt.Turns : dispatch.ProviderUsage?.Turns);

    internal static ProviderUsageValue? RecordedToolCalls(TaskDispatchRecord dispatch) =>
        dispatch.ContextPackageReceipt?.ToolCalls is { State: ProviderUsageState.Reported } receiptValue
            ? receiptValue
            : dispatch.ProviderUsage?.ToolCalls;

    internal static long? ReportedCount(ProviderUsageValue? value) =>
        value is { State: ProviderUsageState.Reported } ? value.Value : null;
}
