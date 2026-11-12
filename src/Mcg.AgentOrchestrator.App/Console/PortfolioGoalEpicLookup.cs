using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class PortfolioGoalEpicLookup(string path) : IOwnerGoalEpicLookup
{
    private PortfolioStore? _store;
    public bool CanReuse { get; private set; }

    public async Task<IReadOnlyDictionary<string, string>> GetTitlesAsync(
        IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken)
    {
        CanReuse = false;
        var reader = new PortfolioGoalEpicTitleReader(path);
        var result = await reader.ReadAsync(goalIds, cancellationToken);
        CanReuse = reader.Succeeded;
        return result;
    }

    public async Task<string> GetTitleAsync(string goalId, CancellationToken cancellationToken)
    {
        if (!File.Exists(path)) return string.Empty;
        try
        {
            _store ??= new PortfolioStore(path);
            return (await _store.GetGoalMembershipAsync(goalId, cancellationToken))?.EpicTitle ?? string.Empty;
        }
        catch (Exception ex) when (ex is SqliteException or IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }
}
