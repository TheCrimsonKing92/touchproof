namespace Mcg.AgentOrchestrator.Core;

public sealed record RoundValueGoalUsageFigures(long? InputTokens, long? CachedInputTokens, long? OutputTokens,
    int Dispatches, int RetryRounds, int ReviewRounds, int UnknownDispatches);

public sealed record RoundValueGoalUsageMeans(double? InputTokens, double? CachedInputTokens, double? OutputTokens,
    double? Dispatches, double? RetryRounds, double? ReviewRounds, double? UnknownDispatches);

public sealed record RoundValueGoalUsageRoleModel(string Role, string? Model, int Dispatches, int UnknownDispatches,
    long? InputTokens, long? CachedInputTokens, long? OutputTokens);

public sealed record RoundValueGoalUsageRow(string GoalId, string Outcome, int Dispatches, int RetryRounds,
    int ReviewRounds, int UnknownDispatches, long? InputTokens, long? CachedInputTokens, long? OutputTokens,
    IReadOnlyList<RoundValueGoalUsageRoleModel> RoleModels);

public sealed record RoundValueGoalUsageRoleModelSummary(string Role, string? Model,
    RoundValueGoalUsageFigures Sums, RoundValueGoalUsageMeans Means);

public sealed record RoundValueGoalUsageLanded(int LandedGoals, int EligibleGoals, RoundValueGoalUsageFigures Sums,
    RoundValueGoalUsageMeans Means, IReadOnlyList<RoundValueGoalUsageRoleModelSummary> RoleModels);

/// <summary>Read-only goal usage from the existing terminal cohort and classified ledger rounds.</summary>
public sealed record RoundValueGoalUsageSlice(DateTimeOffset? Since, DateTimeOffset? Until, string? Goal,
    IReadOnlyList<RoundValueGoalUsageRow> Rows, RoundValueGoalUsageLanded Landed)
{
    public static RoundValueGoalUsageSlice Build(IEnumerable<Goal> goals, DateTimeOffset since, DateTimeOffset until,
        IReadOnlyCollection<AppliedRetryIntent>? intents = null)
    {
        if (since >= until) throw new ArgumentException("The round-value window must end after it starts.");
        var cohort = RoundValueClassifier.Classify(goals, intents ?? []).GroupBy(r => r.Round.GoalId)
            .Where(g => g.First().Outcome != RoundGoalOutcome.Pending &&
                g.Max(r => r.Round.DispatchedAt) >= since && g.Max(r => r.Round.DispatchedAt) < until)
            .OrderBy(g => g.Key, StringComparer.Ordinal).ToArray();
        var rows = cohort.Select(g => Row(g.Key, g.First().Outcome.ToString().ToLowerInvariant(),
            g.Select(r => r.Round).ToArray())).ToArray();
        var landed = cohort.Where(g => g.First().Outcome == RoundGoalOutcome.Landed).ToArray();
        return new(since, until, null, rows, Summarize(landed.Length,
            landed.Count(g => g.Any(r => r.Round.UsageReported)),
            landed.SelectMany(g => g.Select(r => r.Round)).ToArray()));
    }

    public static RoundValueGoalUsageSlice BuildForGoal(Goal goal,
        IReadOnlyCollection<AppliedRetryIntent>? intents = null)
    {
        var classified = RoundValueClassifier.Classify([goal], intents ?? []);
        var rounds = classified.Select(r => r.Round).ToArray();
        var outcome = classified.FirstOrDefault()?.Outcome;
        var landed = outcome == RoundGoalOutcome.Landed ||
            (outcome is null && goal.Status == GoalStatus.Completed);
        var row = Row(goal.Id.Value, outcome is null or RoundGoalOutcome.Pending
            ? goal.Status.ToString().ToLowerInvariant() : outcome.Value.ToString().ToLowerInvariant(), rounds);
        return new(null, null, goal.Id.Value, [row], Summarize(landed ? 1 : 0,
            landed && rounds.Any(r => r.UsageReported) ? 1 : 0, landed ? rounds : []));
    }

    private static RoundValueGoalUsageRow Row(string goalId, string outcome, WorkerRoundRecord[] rounds)
    {
        var sums = Total(rounds);
        var pairs = Pairs(rounds).Select(g =>
        {
            var pair = Total(g.ToArray());
            return new RoundValueGoalUsageRoleModel(g.Key.Role, g.Key.Model, pair.Dispatches,
                pair.UnknownDispatches, pair.InputTokens, pair.CachedInputTokens, pair.OutputTokens);
        }).ToArray();
        return new(goalId, outcome, sums.Dispatches, sums.RetryRounds, sums.ReviewRounds, sums.UnknownDispatches,
            sums.InputTokens, sums.CachedInputTokens, sums.OutputTokens, pairs);
    }

    private static RoundValueGoalUsageLanded Summarize(int landedGoals, int eligibleGoals, WorkerRoundRecord[] rounds)
    {
        var sums = Total(rounds);
        var pairs = Pairs(rounds).Select(g =>
        {
            var pair = Total(g.ToArray());
            return new RoundValueGoalUsageRoleModelSummary(g.Key.Role, g.Key.Model, pair, Mean(pair, eligibleGoals));
        }).ToArray();
        return new(landedGoals, eligibleGoals, sums, Mean(sums, eligibleGoals), pairs);
    }

    private static IOrderedEnumerable<IGrouping<(string Role, string? Model), WorkerRoundRecord>> Pairs(
        IEnumerable<WorkerRoundRecord> rounds) => rounds.GroupBy(r => (Role: r.Role.ToString(), Model: r.ModelName))
        .OrderBy(g => g.Key.Role, StringComparer.Ordinal).ThenBy(g => g.Key.Model, StringComparer.Ordinal);

    private static RoundValueGoalUsageFigures Total(WorkerRoundRecord[] rounds) => new(
        Sum(rounds.Select(r => r.InputTokens)), Sum(rounds.Select(r => r.CachedInputTokens)),
        Sum(rounds.Select(r => r.OutputTokens)), rounds.Length, rounds.Count(r => r.RoundIndex > 1),
        rounds.Count(r => r.Role == AgentRole.Reviewer), rounds.Count(r => !r.UsageReported));

    // Sum each measured component independently; no contributors is different from a reported zero.
    private static long? Sum(IEnumerable<long?> values)
    {
        long? total = null;
        foreach (var value in values)
            if (value is { } reported) total = total is { } previous ? checked(previous + reported) : reported;
        return total;
    }

    private static RoundValueGoalUsageMeans Mean(RoundValueGoalUsageFigures sums, int eligibleGoals)
    {
        double? Divide(long? value) => eligibleGoals == 0 || value is null ? null : (double)value.Value / eligibleGoals;
        return new(Divide(sums.InputTokens), Divide(sums.CachedInputTokens), Divide(sums.OutputTokens),
            Divide(sums.Dispatches), Divide(sums.RetryRounds), Divide(sums.ReviewRounds), Divide(sums.UnknownDispatches));
    }
}
