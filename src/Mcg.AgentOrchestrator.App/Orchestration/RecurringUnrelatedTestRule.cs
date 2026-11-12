using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record RecurringTestPassedGate(string GoalId, string CandidateSha, string MainSha, DateTimeOffset At);
internal sealed record RecurringUnrelatedTestMatch(ApparatusRedFailingTest Test, IReadOnlyList<RecurringTestFailureOccurrence> Occurrences);
internal sealed record RecurringUnrelatedTestVerdict(IReadOnlyList<RecurringUnrelatedTestMatch> RecurringTests);

// Invariant: every identity has positive unrelated evidence; one uncovered identity rejects the gate.
internal static class RecurringUnrelatedTestRule
{
    internal static readonly TimeSpan Window = TimeSpan.FromDays(7);
    internal const int MinimumOtherGoals = 2;
    internal const int PassesToClear = 3;
    internal const int RegateCap = 2;

    internal static RecurringUnrelatedTestVerdict? Evaluate(string goalId, ApparatusRedGateReading reading,
        IReadOnlyList<RecurringTestFailureOccurrence> history, IReadOnlyList<RecurringTestPassedGate> passes,
        Func<string, DateTimeOffset?> closedAt, DateTimeOffset now)
    {
        if (reading.ChangedPaths.Count == 0 || !reading.EveryFailedCheckHasTestIdentities || reading.FailingTests.Count == 0)
            return null;
        var matches = new List<RecurringUnrelatedTestMatch>();
        foreach (var test in reading.FailingTests)
        {
            if (test.ResolvedSourcePaths.Count == 0 || test.InsideChangedPaths || test.ReadsRepositorySources ||
                ApparatusRedClassifier.MessageNamesCandidatePath(test.FailureMessage, reading.ChangedPaths) ||
                CrossGoalTestFailureHistory.TestKey(test.TestIdentity) is not { } key)
                return null;
            var occurrences = history.Where(row => row.TestKey == key && row.At >= now - Window && row.At <= now).ToArray();
            var watermark = ResetWatermark(occurrences, passes, closedAt(key), now);
            var others = occurrences.Where(row => !string.Equals(row.GoalId, goalId, StringComparison.OrdinalIgnoreCase) &&
                (watermark is null || row.At > watermark.Value)).ToArray();
            if (others.Select(row => row.GoalId).Distinct(StringComparer.OrdinalIgnoreCase).Count() >= MinimumOtherGoals)
                matches.Add(new(test, others));
            else if (test.BaselineAttribution is not { Origin: AcceptanceTestFailureOrigin.Inherited } inherited ||
                     string.IsNullOrWhiteSpace(inherited.Evidence))
                return null;
        }
        return matches.Count == 0 ? null : new(matches);
    }

    internal static DateTimeOffset? ResetWatermark(IReadOnlyList<RecurringTestFailureOccurrence> failures,
        IReadOnlyList<RecurringTestPassedGate> passes, DateTimeOffset? closedAt, DateTimeOffset now)
    {
        var watermark = closedAt;
        var events = failures.Select(row => (row.At, Passed: false))
            .Concat(passes.Where(row => row.At <= now)
                .GroupBy(row => (row.GoalId, row.CandidateSha, row.MainSha))
                .Select(group => (group.Min(row => row.At), Passed: true)))
            .GroupBy(row => row.Item1).OrderBy(group => group.Key);
        var streak = 0;
        var seenFailure = false;
        foreach (var group in events)
        {
            // Tied timestamps cannot prove passes came after a failure. Failure wins that instant.
            if (group.Any(row => !row.Passed)) { streak = 0; seenFailure = true; }
            else if (seenFailure)
            {
                streak += group.Count();
                if (streak >= PassesToClear && (watermark is null || group.Key > watermark.Value)) watermark = group.Key;
            }
        }
        return watermark;
    }
}
