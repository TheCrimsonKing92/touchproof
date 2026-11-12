namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal interface IOwnerGoalEpicLookup
{
    bool CanReuse => true;
    Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken);

    async Task<IReadOnlyDictionary<string, string>> GetTitlesAsync(
        IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken)
    {
        var titles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in goalIds.Distinct(StringComparer.Ordinal))
            titles[id] = await GetTitleAsync(id, cancellationToken);
        return titles;
    }
}
