using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record ContextSectionOutcomeRow(
    string Role,
    string Section,
    int DispatchCount,
    int LandedCount,
    int FailedCount,
    int RetriedCount,
    double? LandedShareWithSection,
    int WithoutSectionDispatchCount,
    int WithoutSectionLandedCount,
    double? LandedShareWithoutSection,
    int ReportedInputCount,
    ContextUsageStat InputUsage,
    ContextUsageStat SectionBytes,
    int MissingUsageCount,
    IReadOnlyDictionary<string, int> MissingUsageReasons,
    int UnresolvedCount);

internal sealed record ContextSectionOutcomeResult(
    DateTimeOffset? Since,
    string? Role,
    int DispatchCount,
    int UnresolvedDispatchCount,
    IReadOnlyList<ContextSectionOutcomeRow> Rows);

/// <summary>
/// Read-only correlation over receipt-bearing dispatches; receiptless dispatches enter neither
/// comparison population. A dispatch is retried if a later record exists in its task's complete
/// history, ordered by dispatch timestamp then persisted history index to break ties. Later records
/// count even without receipts or outside role/since filters, regardless of current task status.
/// Otherwise Completed means landed, Failed means failed, and Cancelled means failed only because
/// it did not land (the kernel treats Completed and Cancelled alike for goal completion).
/// The latest dispatch on Pending, Assigned, Running or WaitingForHuman has no outcome: it is
/// tallied separately as unresolved and excluded from outcome counts, both landed-share denominators,
/// and all usage/byte statistics. Since filters dispatches, not the current-state outcome derivation.
/// Missing reported input usage excludes only that input sample, retaining counts, shares and bytes.
/// Outcomes are derived per invocation; no outcome is stored and no state is written.
/// </summary>
internal static class ContextSectionOutcomeReport
{
    private enum Outcome { Landed, Failed, Retried }

    private sealed record Sample(string Role, Outcome? Outcome,
        IReadOnlyDictionary<string, long> Sections, DispatchProviderUsage? Usage);

    internal static ContextSectionOutcomeResult Build(
        IEnumerable<Goal> goals, DateTimeOffset? since = null, string? role = null)
    {
        var samples = goals.SelectMany(goal => goal.Tasks)
            .Where(task => role is null ||
                task.RequiredRole.ToString().Equals(role, StringComparison.OrdinalIgnoreCase))
            .SelectMany(task => Samples(task, since)).ToArray();
        var rows = samples.GroupBy(sample => sample.Role)
            .SelectMany(group => Rows(group.Key, group.ToArray()))
            .OrderBy(row => row.Role, StringComparer.Ordinal)
            .ThenBy(row => row.Section, StringComparer.Ordinal).ToArray();
        return new(since, role, samples.Count(sample => sample.Outcome.HasValue),
            samples.Count(sample => !sample.Outcome.HasValue), rows);
    }

    private static IEnumerable<Sample> Samples(TaskSpec task, DateTimeOffset? since)
    {
        var history = task.DispatchHistory.Select((dispatch, index) => (Dispatch: dispatch, Index: index))
            .OrderBy(item => item.Dispatch.DispatchedAt).ThenBy(item => item.Index).ToArray();
        for (var index = 0; index < history.Length; index++)
        {
            var dispatch = history[index].Dispatch;
            if (dispatch.ContextPackageReceipt is not { } receipt ||
                (since.HasValue && dispatch.DispatchedAt.ToUniversalTime() < since.Value.ToUniversalTime()))
                continue;
            var outcome = index < history.Length - 1 ? Outcome.Retried : LatestOutcome(task.Status);
            var sections = receipt.Sections.GroupBy(section => section.LogicalIdentity, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Sum(section => (long)section.ByteCount),
                    StringComparer.Ordinal);
            yield return new(task.RequiredRole.ToString(), outcome, sections,
                ContextUsageFigures.RecordedUsage(dispatch));
        }
    }

    private static Outcome? LatestOutcome(WorkTaskStatus status) => status switch
    {
        WorkTaskStatus.Completed => Outcome.Landed,
        WorkTaskStatus.Failed or WorkTaskStatus.Cancelled => Outcome.Failed,
        WorkTaskStatus.Pending or WorkTaskStatus.Assigned or WorkTaskStatus.Running or
            WorkTaskStatus.WaitingForHuman => null,
        _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unknown task status.")
    };

    private static IEnumerable<ContextSectionOutcomeRow> Rows(string role, Sample[] samples)
    {
        var resolved = samples.Where(sample => sample.Outcome.HasValue).ToArray();
        var totalLanded = resolved.Count(sample => sample.Outcome == Outcome.Landed);
        foreach (var section in resolved.SelectMany(sample => sample.Sections.Keys).Distinct(StringComparer.Ordinal))
        {
            var included = resolved.Where(sample => sample.Sections.ContainsKey(section)).ToArray();
            var landed = included.Count(sample => sample.Outcome == Outcome.Landed);
            var withoutCount = resolved.Length - included.Length;
            var withoutLanded = totalLanded - landed;
            var missing = included.Where(sample => sample.Usage?.InputTokens.State != ProviderUsageState.Reported)
                .ToArray();
            var reasons = missing.GroupBy(sample => ContextUsageFigures.MissingReasonCode(sample.Usage), StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
            yield return new(role, section, included.Length, landed,
                included.Count(sample => sample.Outcome == Outcome.Failed),
                included.Count(sample => sample.Outcome == Outcome.Retried),
                (double)landed / included.Length, withoutCount, withoutLanded,
                withoutCount == 0 ? null : (double)withoutLanded / withoutCount,
                included.Length - missing.Length,
                ContextUsageFigures.Stat(included.Select(sample => sample.Usage is { } usage
                    ? ContextUsageFigures.Provider(usage.InputTokens).Value : null)),
                ContextUsageFigures.Stat(included.Select(sample => (long?)sample.Sections[section])),
                missing.Length, reasons,
                samples.Count(sample => !sample.Outcome.HasValue && sample.Sections.ContainsKey(section)));
        }
    }
}
