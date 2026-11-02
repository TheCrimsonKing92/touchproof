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
    ContextUsageStat HarnessOverhead,
    int MissingUsageCount,
    IReadOnlyDictionary<string, int> MissingUsageReasons);

internal sealed record ContextUsageResult(
    DateTimeOffset? Since,
    string? Role,
    IReadOnlyList<ContextUsageRow> Rows);

internal static class ContextUsageReport
{
    internal static ContextUsageResult Build(
        IEnumerable<Goal> goals, DateTimeOffset? since = null, string? role = null)
    {
        var groups = goals.SelectMany(goal => goal.Tasks)
            .Where(task => role is null ||
                task.RequiredRole.ToString().Equals(role, StringComparison.OrdinalIgnoreCase))
            .SelectMany(task => task.DispatchHistory.Select(dispatch => (Task: task, Dispatch: dispatch)))
            .Where(item => since is null || item.Dispatch.DispatchedAt.ToUniversalTime() >= since.Value.ToUniversalTime())
            .GroupBy(item => (Role: item.Task.RequiredRole.ToString(),
                Model: string.IsNullOrWhiteSpace(item.Dispatch.ModelName) ? "unknown" : item.Dispatch.ModelName!));

        var rows = groups.Select(group =>
        {
            var receipts = group.Select(item => item.Dispatch.ContextPackageReceipt).ToArray();
            var usage = group.Select(item => ContextUsageFigures.RecordedUsage(item.Dispatch)).ToArray();
            var missing = usage.Where(value => value?.InputTokens.State != ProviderUsageState.Reported).ToArray();
            var reasons = missing.GroupBy(ContextUsageFigures.MissingReasonCode, StringComparer.Ordinal)
                .OrderBy(reason => reason.Key, StringComparer.Ordinal)
                .ToDictionary(reason => reason.Key, reason => reason.Count(), StringComparer.Ordinal);
            return new ContextUsageRow(
                group.Key.Role,
                group.Key.Model,
                group.Count(),
                usage.Count(value => value?.InputTokens.State == ProviderUsageState.Reported),
                Stat(usage.Select(value => value is null ? null : ContextUsageFigures.Provider(value.InputTokens).Value)),
                Stat(usage.Select(value => value is null ? null : ContextUsageFigures.Provider(value.CachedInputTokens).Value)),
                Stat(usage.Select(value => value is null ? null : ContextUsageFigures.Uncached(value.InputTokens, value.CachedInputTokens).Value)),
                Stat(usage.Select(value => value is null ? null : ContextUsageFigures.Provider(value.OutputTokens).Value)),
                Stat(receipts.Select(receipt => receipt is null ? null : ContextUsageFigures.HarnessOverhead(receipt).Value)),
                missing.Length,
                reasons);
        })
        .OrderBy(row => row.Role, StringComparer.Ordinal)
        .ThenBy(row => row.Model == "unknown" ? 1 : 0)
        .ThenBy(row => row.Model, StringComparer.Ordinal)
        .ToArray();
        return new(since, role, rows);
    }

    private static ContextUsageStat Stat(IEnumerable<long?> values)
    {
        var sample = values.Where(value => value.HasValue)
            .Select(value => value!.Value).Order().ToArray();
        if (sample.Length == 0)
            return new(null, null);

        var middle = sample.Length / 2;
        var median = sample.Length % 2 == 1
            ? sample[middle]
            : ((double)sample[middle - 1] + sample[middle]) / 2;
        var p90Index = Math.Clamp((int)Math.Ceiling(0.9 * sample.Length) - 1, 0, sample.Length - 1);
        return new(median, sample[p90Index]);
    }
}
