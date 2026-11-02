using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal readonly record struct ContextUsageFigure(long? Value, string? UnknownReason = null)
{
    public string Display => Value?.ToString(CultureInfo.InvariantCulture) ??
        $"unknown({UnknownReason ?? "not recorded"})";
}

internal static class ContextUsageFigures
{
    internal static DispatchProviderUsage? RecordedUsage(TaskDispatchRecord dispatch) =>
        dispatch.ContextPackageReceipt is { } receipt
            ? new(receipt.InputTokens, receipt.CachedInputTokens, receipt.OutputTokens)
            : dispatch.ProviderUsage;

    internal static string MissingReasonCode(DispatchProviderUsage? usage)
    {
        if (usage is null)
            return "not-recorded";
        var reason = usage.InputTokens.UnknownReason;
        var code = reason?.Split(':', 2)[0].Trim();
        return string.IsNullOrWhiteSpace(code) ? "unspecified" : code;
    }

    internal static ContextUsageFigure Provider(ProviderUsageValue value) =>
        value.State == ProviderUsageState.Reported
            ? new(value.Value)
            : new(null, value.UnknownReason);

    internal static ContextUsageFigure Uncached(WorkerContextPackageReceipt receipt) =>
        Uncached(receipt.InputTokens, receipt.CachedInputTokens);

    internal static ContextUsageFigure Uncached(ProviderUsageValue inputTokens, ProviderUsageValue cachedTokens)
    {
        var input = Provider(inputTokens);
        var cached = Provider(cachedTokens);
        return input.Value is { } inputValue && cached.Value is { } cachedValue
            ? new(inputValue - cachedValue)
            : new(null, input.UnknownReason ?? cached.UnknownReason);
    }

    internal static ContextUsageFigure HarnessOverhead(WorkerContextPackageReceipt receipt)
    {
        var input = Provider(receipt.InputTokens);
        if (input.Value is null)
            return input;
        return receipt.ModelInputTokenEstimate > 0
            ? new(input.Value.Value - receipt.ModelInputTokenEstimate)
            : new(null, "no model input estimate");
    }

    internal static ContextUsageFigure Estimate(WorkerContextPackageReceipt receipt) =>
        receipt.ModelInputTokenEstimate > 0
            ? new(receipt.ModelInputTokenEstimate)
            : new(null, "no model input estimate");

    internal static ContextUsageFigure PromptBytes(WorkerContextPackageReceipt receipt) =>
        receipt.RenderedPromptBytes > 0
            ? new(receipt.RenderedPromptBytes)
            : new(null, "not recorded");
}
