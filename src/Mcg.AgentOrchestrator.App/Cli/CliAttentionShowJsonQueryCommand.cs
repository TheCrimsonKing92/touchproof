using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliAttentionShowJsonQueryCommand
{
    internal static bool IsAttentionShowJsonCommand(IReadOnlyList<string> args) =>
        args.Count is 3 or 4 or 5 &&
        args[0].Equals("attention", StringComparison.OrdinalIgnoreCase) &&
        args[1].Equals("show", StringComparison.OrdinalIgnoreCase) && args[^1] == "--json" &&
        (args.Count == 3 ||
         args.Count == 4 && !args[2].StartsWith("--", StringComparison.Ordinal) ||
         args.Count == 5 && args[2].Equals("--goal", StringComparison.OrdinalIgnoreCase) &&
         !args[3].StartsWith("--", StringComparison.Ordinal)) &&
        !CliCommandHelp.IsCommandSpecificHelp(args);

    internal static void Execute(IReadOnlyList<string> args, IOrchestratorStateQueries stateQueries,
        OrchestratorWorkspace workspace, TimeProvider? time = null, ICollaborationItemStore? collaboration = null)
    {
        var metadata = stateQueries.ListGoalMetadataAsync().GetAwaiter().GetResult();
        var prefix = args.Count == 3 ? null : args[^2];
        var matches = prefix is null ? metadata : metadata
            .Where(goal => goal.Id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (prefix is not null)
        {
            if (matches.Count == 0)
                throw new KeyNotFoundException($"No goal found matching prefix '{prefix}'.");
            if (matches.Count > 1)
                throw new InvalidOperationException($"Goal prefix '{prefix}' is ambiguous ({matches.Count} matches).");
        }
        var ids = matches.Select(goal => new GoalId(goal.Id)).ToArray();
        var kernel = ids.Length == 0
            ? new AgentOrchestratorKernel()
            : stateQueries.LoadGoalsAsync(ids).GetAwaiter().GetResult();
        var goal = prefix is null ? null : CliCommandHandlers.ResolveAttentionGoal(kernel, prefix);
        var itemsDb = Path.Combine(workspace.OrchestratorDirectory, "collaboration-items.db");
        IReadOnlyList<CollaborationItem> items = [];
        IReadOnlyList<CollaborationItem> identityUniverse = [];
        var store = collaboration ?? (File.Exists(itemsDb)
            ? CollaborationItemStore.OpenExisting(workspace.OrchestratorDirectory) : null);
        if (store is not null)
        {
            if (goal is null)
            {
                items = store.GetAttentionQueueAsync().GetAwaiter().GetResult();
                identityUniverse = store.ListAsync().GetAwaiter().GetResult()
                    .Where(CliCommandHandlers.IsSpecClarification).ToArray();
            }
            else
            {
                var rows = store.ListAsync(goal.Id.Value).GetAwaiter().GetResult();
                items = CollaborationItemLifecycle.BuildAttentionQueue(rows);
                // Resolved clarifications still participate in stable clarification identities.
                identityUniverse = rows.Where(CliCommandHandlers.IsSpecClarification).ToArray();
            }
        }
        var waits = CliCommandHandlers.HumanWaitsForAttention(kernel, goal?.Id, includeHistory: false);
        items = CliCommandHandlers.CollaborationItemsForAttention(items, kernel, goal?.Id, includeHistory: false);
        var decisions = prefix is null
            ? OwnerDecisionReader.ReadAsync(OwnerQuestionReadModelFactory.ForWorkspace(workspace)).GetAwaiter().GetResult()
            : null;
        Console.WriteLine(AttentionShowAgentReadView.Render(goal?.Id.Value, waits, items,
            item => CliCommandHandlers.IsSpecClarification(item)
                ? CliCommandHandlers.ClarificationId(item, identityUniverse) : null, time, kernel.Goals, decisions,
            PlannerBriefAmendmentHoldAttentionView.Holds(kernel, kernel.Goals.Where(candidate => goal is null || candidate.Id == goal.Id))));
    }
}
