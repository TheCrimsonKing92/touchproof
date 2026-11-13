using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliInspectionQueryCommand
{
    internal static bool IsInspectionQueryCommand(IReadOnlyList<string> args)
    {
        if (args.Count == 0 || CliCommandHelp.IsCommandSpecificHelp(args))
            return false;

        return args[0].ToLowerInvariant() switch
        {
            "agents" => args.Count == 1,
            "doctor" or "tenant" => args.Count == 1,
            "goals" => args.Count == 1 || (args.Count == 2 && args[1] == "--json") ||
                (!args[1].Equals("subscribe", StringComparison.OrdinalIgnoreCase) &&
                 !args[1].Equals("--board", StringComparison.OrdinalIgnoreCase) &&
                 args.Skip(1).Any(arg => arg.Equals("--no-epic", StringComparison.OrdinalIgnoreCase))),
            "model-outcomes" or "architecture" or "backlog-view" => args.Count == 1,
            "config" => args.Count == 2 && args[1].ToLowerInvariant() is "agents" or "profiles" or "policy" or "doctor",
            _ => false
        };
    }

    internal static void Execute(
        IReadOnlyList<string> args,
        IOrchestratorStateQueries stateRepository,
        OrchestratorWorkspace workspace,
        IModelProviderRegistry providers,
        ref IReadOnlyList<AgentDefinition> agents,
        ref WorkerProfileCatalog workerProfiles)
    {
        switch (args[0].ToLowerInvariant())
        {
            case "goals":
                ValidateGoalsArguments(args);
                var epics = EpicMembershipIndex.Load(workspace.PortfolioStorePath);
                var goals = stateRepository.ListGoalMetadataAsync().GetAwaiter().GetResult();
                if (args.Contains("--no-epic", StringComparer.OrdinalIgnoreCase))
                    goals = goals.Where(goal => epics.FindGoal(goal.Id) is null).ToArray();
                if (args.Contains("--json", StringComparer.OrdinalIgnoreCase))
                {
                    Console.WriteLine(GoalsAgentReadView.Serialize(goals, epics));
                    return;
                }
                GoalListTextView.Print(goals, epics);
                var debtHooks = WorktreeCleanupContext.Load(
                    attentionStoreDirectory: workspace.OrchestratorDirectory).Hooks;
                GoalListTextView.PrintCleanupDebtWarning(
                    GoalWorktrees.ListCleanupDebt(workspace.ExecutionDirectory, debtHooks));
                break;
            case "model-outcomes":
                ModelOutcomeBoundModels.Print(
                    stateRepository.BuildModelOutcomeScorecardAsync().GetAwaiter().GetResult(), workspace);
                break;
            default:
                Goal? commandGoal = null;
                CliReadOnlyQueryExecutor.Execute(args, new AgentOrchestratorKernel(), workspace,
                    providers, ref agents, ref workerProfiles, ref commandGoal);
                break;
        }
    }

    private static void ValidateGoalsArguments(IReadOnlyList<string> args)
    {
        var flags = args.Skip(1).ToArray();
        if (flags.Count(arg => arg.Equals("--no-epic", StringComparison.OrdinalIgnoreCase)) > 1 ||
            flags.Count(arg => arg.Equals("--json", StringComparison.OrdinalIgnoreCase)) > 1 ||
            flags.Any(arg => !arg.Equals("--no-epic", StringComparison.OrdinalIgnoreCase) &&
                             !arg.Equals("--json", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException(CliCommandHelp.GoalsUsage);
    }
}
