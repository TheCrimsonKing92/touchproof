using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerConsoleGoalDetail
{
    internal static async Task<string?> ResolveAsync(IOrchestratorStateQueries state,
        string idOrPrefix, IOwnerConsoleOutput output, CancellationToken cancellationToken)
    {
        var matches = (await state.ListGoalMetadataAsync(cancellationToken))
            .Where(item => item.Id.StartsWith(idOrPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        // A board selection supplies an exact id, even when that id prefixes another goal.
        var exact = matches.FirstOrDefault(item => item.Id.Equals(idOrPrefix, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) matches = [exact];
        if (matches.Length != 1)
        {
            output.WriteLine(matches.Length == 0 ? $"no goal matches '{idOrPrefix}'" : "ambiguous goal");
            var rows = new Dictionary<int, OwnerConsoleFieldSnapshot>();
            var line = 1;
            foreach (var match in matches)
            {
                var title = OwnerGoalTitle.From(match.Objective);
                var fields = OwnerConsoleFieldSnapshot.Create(match.Id,
                    new("goal", "Goal ID", match.Id), new("title", "Title", title));
                rows[line++] = fields;
                output.WriteLine(match.Id[..Math.Min(8, match.Id.Length)] + "  " + title);
            }
            if (output is OwnerConsoleCapturedOutput captured && rows.Count > 0)
                captured.Structured = new(null, rows);
            return null;
        }
        return matches[0].Id;
    }

    internal static string Stage(Goal goal) =>
        goal.Tasks.FirstOrDefault(task => task.Status != WorkTaskStatus.Completed)?.RequiredRole.ToString() ?? "-";
}
