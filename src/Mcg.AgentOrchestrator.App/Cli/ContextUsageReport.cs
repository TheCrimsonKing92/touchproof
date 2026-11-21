using Mcg.AgentOrchestrator.App.Usage;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record ContextUsageStat(double? Median, long? P90);

internal sealed record ContextUsageRow(
    string Role,
    string Model,
    int DispatchCount,
    int ReportedInputCount,
    ContextUsageStat Input,
    ContextUsageStat Cached,
    ContextUsageStat Uncached,
    ContextUsageStat Output,
    ContextUsageStat Turns,
    ContextUsageStat ToolCalls,
    ContextUsageStat HarnessOverhead,
    int MissingUsageCount,
    IReadOnlyDictionary<string, int> MissingUsageReasons);

internal sealed record ContextUsageResult(
    DateTimeOffset? Since,
    string? Role,
    IReadOnlyList<ContextUsageRow> Rows,
    ContextUsageCoverage Coverage);

internal sealed record ContextUsageCoverage(
    int Live,
    int Archived,
    int UnknownUsage,
    int Conflicting,
    IReadOnlyList<DispatchUsageIdentity> ConflictIds);

internal static class ContextUsageReport
{
    internal static ContextUsageResult Build(
        IEnumerable<Goal> goals, DateTimeOffset? since = null, string? role = null) =>
        Build(DispatchUsagePopulation.Build(goals, since, role), since, role);

    internal static ContextUsageResult Build(DispatchUsagePopulation population, DateTimeOffset? since, string? role)
    {
        var groups = population.Dispatches
            .GroupBy(item => (Role: item.Role.ToString(),
                Model: string.IsNullOrWhiteSpace(item.Dispatch.ModelName) ? "unknown" : item.Dispatch.ModelName!));

        var rows = groups.Select(group =>
        {
            var receipts = group.Select(item => item.Dispatch.ContextPackageReceipt).ToArray();
            var usage = group.Select(item => DispatchUsageValues.RecordedUsage(item.Dispatch)).ToArray();
            var missing = usage.Where(value => value?.InputTokens.State != ProviderUsageState.Reported).ToArray();
            var reasons = missing.GroupBy(ContextUsageFigures.MissingReasonCode, StringComparer.Ordinal)
                .OrderBy(reason => reason.Key, StringComparer.Ordinal)
                .ToDictionary(reason => reason.Key, reason => reason.Count(), StringComparer.Ordinal);
            return new ContextUsageRow(
                group.Key.Role,
                group.Key.Model,
                group.Count(),
                usage.Count(value => value?.InputTokens.State == ProviderUsageState.Reported),
                Stat(usage.Select(value => DispatchUsageValues.ReportedCount(value?.InputTokens))),
                Stat(usage.Select(value => DispatchUsageValues.ReportedCount(value?.CachedInputTokens))),
                Stat(usage.Select(value => value is null ? null : ContextUsageFigures.Uncached(value.InputTokens, value.CachedInputTokens).Value)),
                Stat(usage.Select(value => DispatchUsageValues.ReportedCount(value?.OutputTokens))),
                Stat(group.Select(item => RecordedTurns(item.Dispatch))),
                Stat(group.Select(item => RecordedToolCalls(item.Dispatch))),
                Stat(receipts.Select(receipt => receipt is null ? null : ContextUsageFigures.HarnessOverhead(receipt).Value)),
                missing.Length,
                reasons);
        })
        .OrderBy(row => row.Role, StringComparer.Ordinal)
        .ThenBy(row => row.Model == "unknown" ? 1 : 0)
        .ThenBy(row => row.Model, StringComparer.Ordinal)
        .ToArray();
        return new(since, role, rows, new(population.Live, population.Archived,
            rows.Sum(row => row.MissingUsageCount), population.Conflicts.Count, population.Conflicts));
    }

    internal static long? RecordedTurns(TaskDispatchRecord dispatch) => DispatchUsageValues.RecordedTurns(dispatch);

    private static long? RecordedToolCalls(TaskDispatchRecord dispatch)
    {
        var toolCalls = DispatchUsageValues.RecordedToolCalls(dispatch);
        return toolCalls is null ? null : ContextUsageFigures.Provider(toolCalls).Value;
    }

    private static ContextUsageStat Stat(IEnumerable<long?> values) => ContextUsageFigures.Stat(values);
}
