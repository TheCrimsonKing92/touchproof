namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillDailyAllowance
{
    internal static int Count(IEnumerable<BoardFillDraftRound> rounds, DateTimeOffset now) =>
        rounds.Count(round => round.Outcome != "held" && round.StartedAt.UtcDateTime.Date == now.UtcDateTime.Date &&
            !(round.Filings ?? []).Any(attempt => attempt.Terminal &&
                string.Equals(attempt.Reason, "not-fileable:backlog-changed", StringComparison.Ordinal)));
}
