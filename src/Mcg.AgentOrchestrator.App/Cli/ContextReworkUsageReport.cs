using System.Text.Json;
using Mcg.AgentOrchestrator.App.Usage;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed record ContextReworkCoverage(int Live, int ArchivedHydrated, int ArchivedUnavailable);

internal sealed record ContextReworkUsageTotals(int Attempts, long? Input, long? Cached, long? Output, int MissingUsage);

internal sealed record ContextReworkExample(string GoalId, string TaskId);

internal sealed record ContextReworkUsageRow(
    string Role, string RetryCause, string FindingClass,
    int Attempts, long? Input, long? Cached, long? Output, int MissingUsage,
    int GoalsAffected, double? MedianInput, int MedianGoalCount, IReadOnlyList<ContextReworkExample> Examples);

internal sealed record ContextReworkUsageResult(
    DateTimeOffset? Since, DateTimeOffset? Until, string? Role, int Top,
    ContextReworkCoverage Coverage, ContextReworkUsageTotals Overall, IReadOnlyList<ContextReworkUsageRow> Rows,
    IReadOnlyList<string> Notes);

internal static class ContextReworkUsageReport
{
    internal static ContextReworkUsageResult Build(
        IEnumerable<Goal> goals, DateTimeOffset? since, DateTimeOffset? until, string? role,
        ContextReworkCoverage coverage, int top = 10)
    {
        var attempts = goals.Where(goal => !goal.IsMetadataOnly).DistinctBy(goal => goal.Id)
            .SelectMany(goal => ReadAttempts(goal, since, until, role)).ToArray();
        var rows = attempts
            .SelectMany(attempt => attempt.Classes.Select(findingClass => (Attempt: attempt, Class: findingClass)))
            .GroupBy(item => (item.Attempt.Role, item.Attempt.Cause, item.Class), item => item.Attempt)
            .Select(group => BuildRow(group.Key.Role, group.Key.Cause, group.Key.Class, group.ToArray()))
            .OrderByDescending(row => row.Input)
            .ThenBy(row => row.Role, StringComparer.Ordinal)
            .ThenBy(row => row.RetryCause, StringComparer.Ordinal)
            .ThenBy(row => row.FindingClass, StringComparer.Ordinal)
            .ToArray();
        return new(since, until, role, top, coverage, Sum(attempts), rows,
        [
            "Finding-class rows are not additive when a retry has multiple classes. Overall counts each retry once.",
            "Gate-derived test classes are unclassified when no structured class is recorded.",
            "Contract clarification receipts may include other findings that were open at admission.",
            "Missing usage counts attempts with any unreported token field. Medians use goals with complete input telemetry."
        ]);
    }

    private static IEnumerable<Attempt> ReadAttempts(
        Goal goal, DateTimeOffset? since, DateTimeOffset? until, string? role)
    {
        // Index occurrences once per goal. Only receipt-linked IDs can drive attribution.
        var findings = new Lazy<ILookup<string, (DateTimeOffset CompletedAt, ReviewFinding Finding)>>(() =>
            goal.Tasks.SelectMany(task => task.VerificationHistory)
                .OrderByDescending(record => record.CompletedAt)
                .SelectMany(record => (record.MergedReviewFindings ?? [])
                    .Select(finding => (record.CompletedAt, Finding: finding)))
                .ToLookup(item => item.Finding.StableId, StringComparer.Ordinal));

        foreach (var task in goal.Tasks)
        {
            var taskRole = task.RequiredRole.ToString();
            if (role is not null && !taskRole.Equals(role, StringComparison.OrdinalIgnoreCase))
                continue;

            var retries = task.DispatchHistory.OrderBy(dispatch => dispatch.DispatchedAt)
                .DistinctBy(dispatch => dispatch.DispatchedAt).Skip(1)
                .Where(dispatch => (since is null || dispatch.DispatchedAt >= since) &&
                                   (until is null || dispatch.DispatchedAt < until))
                .ToArray();
            if (retries.Length == 0)
                continue;

            var receipts = task.RetryAdmissionHistory
                .Where(receipt => receipt.Decision != RetryAdmissionDecision.Prevented)
                .GroupBy(receipt => receipt.LinkedDispatchAt)
                .ToDictionary(group => group.Key, group => group.OrderBy(receipt => receipt.RecordedAt).Last());

            foreach (var retry in retries)
            {
                receipts.TryGetValue(retry.DispatchedAt, out var receipt);
                var usage = DispatchUsageValues.RecordedUsage(retry);
                yield return new(goal.Id.Value, task.Id.Value, taskRole, receipt?.Cause ?? RetryCause.Unknown,
                    Classify(receipt, retry.DispatchedAt, findings),
                    DispatchUsageValues.ReportedCount(usage?.InputTokens),
                    DispatchUsageValues.ReportedCount(usage?.CachedInputTokens),
                    DispatchUsageValues.ReportedCount(usage?.OutputTokens));
            }
        }
    }

    private static string[] Classify(RetryAdmissionReceipt? receipt, DateTimeOffset dispatchedAt,
        Lazy<ILookup<string, (DateTimeOffset CompletedAt, ReviewFinding Finding)>> findings)
    {
        if (receipt?.Cause is RetryCause.NewSourceFinding or RetryCause.NewTestFinding or RetryCause.ContractClarification &&
            receipt.StableFindingIds is { Count: > 0 } ids)
        {
            var classes = ids.Select(id => findings.Value[id].FirstOrDefault(item => item.CompletedAt <= dispatchedAt).Finding)
                .Select(finding => finding switch
                {
                    { ClassId: not FindingClass.None } => $"reviewer-class:{JsonSerializer.SerializeToElement(finding.ClassId).GetString()}",
                    { Category: not FindingCategory.Unspecified } => $"reviewer:{FindingCategoryJsonConverter.ToWireValue(finding.Category)}",
                    _ => null
                })
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (classes.Length > 0)
                return classes;
        }
        return ["unclassified"];
    }

    private static ContextReworkUsageRow BuildRow(string role, RetryCause cause, string findingClass, Attempt[] attempts)
    {
        var totals = Sum(attempts);
        var goals = attempts.GroupBy(attempt => attempt.GoalId).ToArray();
        var completeGoalInputs = goals.Where(goal => goal.All(attempt => attempt.Input.HasValue))
            .Select(goal => goal.Sum(attempt => attempt.Input)).ToArray();

        var examples = attempts.Select(attempt => new ContextReworkExample(attempt.GoalId, attempt.TaskId))
            .Distinct()
            .OrderBy(pair => pair.GoalId, StringComparer.Ordinal)
            .ThenBy(pair => pair.TaskId, StringComparer.Ordinal)
            .GroupBy(pair => pair.GoalId)
            .SelectMany(goal => goal.Select((pair, index) => (Pair: pair, Index: index)))
            .OrderBy(item => item.Index)
            .Take(3)
            .Select(item => item.Pair)
            .ToArray();

        return new(role, cause.ToString(), findingClass, totals.Attempts, totals.Input, totals.Cached, totals.Output,
            totals.MissingUsage, goals.Length, ContextUsageFigures.Stat(completeGoalInputs).Median,
            completeGoalInputs.Length, examples);
    }

    private static ContextReworkUsageTotals Sum(Attempt[] attempts) =>
        attempts.Length == 0 ? new(0, 0, 0, 0, 0) : new(attempts.Length,
            SumReported(attempts.Select(attempt => attempt.Input)),
            SumReported(attempts.Select(attempt => attempt.Cached)),
            SumReported(attempts.Select(attempt => attempt.Output)),
            attempts.Count(attempt => attempt.Input is null || attempt.Cached is null || attempt.Output is null));

    private static long? SumReported(IEnumerable<long?> counts)
    {
        long? total = null;
        foreach (var count in counts)
        {
            if (count is { } reported)
                total = checked(total.GetValueOrDefault() + reported);
        }
        return total;
    }

    private sealed record Attempt(string GoalId, string TaskId, string Role, RetryCause Cause, string[] Classes,
        long? Input, long? Cached, long? Output);
}
