using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliProvenanceQueryCommand
{
    internal static bool IsProvenanceQueryCommand(IReadOnlyList<string> args) =>
        !CliCommandHelp.IsCommandSpecificHelp(args) &&
        args.Count == 1 && args[0].Equals("provenance", StringComparison.OrdinalIgnoreCase);

    internal static void Execute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        IOperatorChannel? channel,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles)
    {
        var completedGoalIds = stateQueries.ListGoalIdStatusesAsync().GetAwaiter().GetResult()
            .Where(goal => goal.Status.Equals(GoalStatus.Completed.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(goal => new GoalId(goal.Id))
            .ToArray();
        var snapshots = new Dictionary<string, GoalSnapshot>(StringComparer.Ordinal);
        foreach (var batch in completedGoalIds.Chunk(500))
        {
            var loaded = stateQueries.LoadGoalsAsync(batch).GetAwaiter().GetResult();
            foreach (var goal in loaded.Goals)
                snapshots[goal.Id.Value] = loaded.ExportGoalSnapshot(goal.Id);
        }

        // Bulk reads do not guarantee row order; preserve the previous metadata listing order.
        var kernel = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot(
            completedGoalIds.Where(id => snapshots.ContainsKey(id.Value))
                .Select(id => snapshots[id.Value]).ToArray(), []));
        Goal? localGoal = null;
        CliReadOnlyQueryExecutor.Execute(args, kernel, workspace, providers, channel,
            ref agents, ref workerProfiles, ref localGoal);
    }
}
