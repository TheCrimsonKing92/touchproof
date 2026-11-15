namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class BoardFillConsecutiveNonDraftRounds
{
    internal const int Limit = 3;

    internal static IReadOnlyDictionary<string, int> StoppedItems(
        IEnumerable<BoardFillDraftRound> rounds, DateTimeOffset now)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        // OrderBy is stable: equal timestamps retain the caller's insertion order.
        foreach (var round in rounds.Where(round => round.StartedAt.UtcDateTime.Date == now.UtcDateTime.Date)
                     .OrderBy(round => round.StartedAt))
        {
            if (round.Outcome is "stale" or "failed")
                counts[round.BacklogItemId] = counts.GetValueOrDefault(round.BacklogItemId) + 1;
            else if (round.Outcome == "draft")
                counts[round.BacklogItemId] = 0;
        }
        return counts.Where(pair => pair.Value >= Limit)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
    }
}
