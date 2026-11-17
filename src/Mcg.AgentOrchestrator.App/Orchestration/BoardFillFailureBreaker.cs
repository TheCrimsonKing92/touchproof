namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillFailureBreaker
{
    internal const int Threshold = 3;
    internal const int InitialDelayMinutes = 5;
    internal const int MaxDelayMinutes = 60;

    internal sealed record Backoff(int ConsecutiveCount, TimeSpan Delay, DateTimeOffset RetryAt, string RoundId);

    internal static Backoff? Evaluate(IEnumerable<BoardFillDraftRound> rounds, DateTimeOffset now)
    {
        var count = 0;
        BoardFillDraftRound? latest = null;
        // Stable ordering retains persisted insertion order when completion timestamps tie.
        foreach (var round in rounds.Where(round => round.FinishedAt is not null && round.Outcome is not null)
                     .OrderBy(round => round.FinishedAt))
        {
            if (round.Outcome == "draft")
            {
                count = 0;
                latest = null;
            }
            else if (round.Outcome is "stale" or "failed")
            {
                count++;
                latest = round;
            }
        }
        if (count < Threshold || latest is null) return null;
        // Four doublings already reach the cap; clamp before shifting to avoid overflow.
        var delay = TimeSpan.FromMinutes(Math.Min(MaxDelayMinutes,
            InitialDelayMinutes * (1 << Math.Min(count - Threshold, 4))));
        var retryAt = latest.FinishedAt!.Value + delay;
        return now < retryAt ? new(count, delay, retryAt, latest.Id) : null;
    }
}
