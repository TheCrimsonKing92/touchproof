using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// An occurrence is evidence of a gate failure, never a Tester narrative or a diagnosis.
internal sealed record RecurringTestFailureOccurrence(
    string TestKey, string GoalId, DateTimeOffset At, string Source,
    string? AttemptId, string? Lane, string? CandidateSha, string? MessageFingerprint);

internal sealed class RecurringTestFailureOccurrenceReader(string? censusPath, string? stateRoot)
{
    internal IReadOnlyList<RecurringTestFailureOccurrence> Read(DateTimeOffset now)
    {
        IReadOnlyList<AcceptanceFailingTestIndexRecord> census = censusPath is null ? [] : new AcceptanceFailingTestIndex(censusPath).Read();
        IReadOnlyList<AcceptanceLaneFlakeRow> flakes = [];
        try
        {
            if (stateRoot is not null)
                flakes = new AcceptanceLaneFlakeLedger(stateRoot).Read(since: now - RecurringUnrelatedTestRule.Window);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return Read(census, flakes, now);
    }

    internal static IReadOnlyList<RecurringTestFailureOccurrence> Read(
        IReadOnlyList<AcceptanceFailingTestIndexRecord> census,
        IReadOnlyList<AcceptanceLaneFlakeRow> flakes, DateTimeOffset now)
    {
        bool InWindow(DateTimeOffset at) => at <= now && at >= now - RecurringUnrelatedTestRule.Window;
        var failures = census.Where(row => row.Kind == AcceptanceFailingTestIndexKinds.GateFailure && InWindow(row.RecordedAt)).ToArray();
        // A lane row cannot overrule evidence that that goal changed the test itself.
        var touched = failures.Where(row => row.InsideChangedPaths)
            .Select(row => (Goal: row.GoalId.ToLowerInvariant(), Key: CrossGoalTestFailureHistory.TestKey(row.TestIdentity))).ToHashSet();
        var result = new List<RecurringTestFailureOccurrence>();
        foreach (var row in failures)
            if (!row.InsideChangedPaths && CrossGoalTestFailureHistory.TestKey(row.TestIdentity) is { } key &&
                !touched.Contains((row.GoalId.ToLowerInvariant(), key)))
                result.Add(new(key, row.GoalId, row.RecordedAt, "census", null, row.CheckName, row.CandidateSha, row.MessageFingerprint));
        foreach (var row in flakes.Where(row => InWindow(row.RecordedAt) &&
                     row.Outcome is AcceptanceLaneRerunEvidence.Flake or AcceptanceLaneRerunEvidence.ConfirmedFailure))
            foreach (var identity in row.FirstFailingTestIdentities)
                if (CrossGoalTestFailureHistory.TestKey(identity) is { } key && !touched.Contains((row.GoalId.ToLowerInvariant(), key)))
                    result.Add(new(key, row.GoalId, row.RecordedAt, "lane-flake", row.AttemptId, row.LaneName, row.CandidateTreeSha, null));
        return result;
    }
}
