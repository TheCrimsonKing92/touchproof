using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionDismissCommand
{
    internal static void Execute(IReadOnlyList<string> parts, CliExecutionContext context, CollaborationItemStore store)
    {
        if (parts.Count == 4 && parts[2].Equals("--item", StringComparison.OrdinalIgnoreCase))
        {
            var item = FindAttentionItemById(
                store.ListAsync().GetAwaiter().GetResult(),
                parts[3]);
            if (item is null)
            {
                var requestId = parts[3];
                var request = context.Kernel.HumanInputRequests.SingleOrDefault(candidate => candidate.Id.Value == requestId);
                if (request is null)
                    throw new ArgumentException(Guid.TryParseExact(requestId, "N", out _)
                        ? $"No attention item or human-input request matches id '{requestId}'."
                        : $"No attention item matches id '{requestId}'.");
                HumanInputDismissIntentHandler.ValidateOpenDismissible(request);
                Queue(context, parts, request);
                return;
            }

            if (CollaborationItemLifecycle.IsTerminal(item.Status))
            {
                throw new InvalidOperationException(
                    $"Attention item '{parts[3]}' matched, but status is '{item.Status}'; nothing was dismissed.");
            }

            if (!store.TryResolveByIdAsync(item.Id, "dismissed by operator").GetAwaiter().GetResult())
            {
                throw new InvalidOperationException(
                    $"Attention item '{parts[3]}' matched, but it was no longer open when dismissal was applied; nothing was dismissed.");
            }

            Console.WriteLine($"Dismissed attention item '{item.Id}'.");
            return;
        }

        var explicitGoal = parts.Count == 4 && parts[2].Equals("--goal", StringComparison.OrdinalIgnoreCase);
        if ((parts.Count != 3 || parts[2].StartsWith("--", StringComparison.Ordinal)) && !explicitGoal)
            throw new ArgumentException(CliCommandHelp.AttentionUsage);

        var goalPrefix = explicitGoal ? parts[3] : parts[2];
        var goal = AttentionReadGoalSelection.Resolve(context.Kernel, goalPrefix);
        var open = store.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item => string.Equals(item.GoalId, goal.Id.Value, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var dismissed = open.Count(item =>
            store.TryResolveByIdAsync(item.Id, "dismissed by operator").GetAwaiter().GetResult());
        var requests = context.Kernel.HumanInputRequests
            .Where(request => request.GoalId == goal.Id && !request.IsCompleted).ToArray();
        var queued = 0;
        foreach (var request in requests.Where(request => request.IsDismissible))
        {
            Queue(context, parts, request);
            queued++;
        }

        if (open.Count > 0 && dismissed == 0 && queued == 0)
        {
            throw new InvalidOperationException(
                $"Goal '{goal.Id.Value}' matched {open.Count} open attention item(s), but none remained eligible for dismissal.");
        }

        Console.WriteLine($"Dismissed {dismissed} of {open.Count} open attention item(s) for goal '{goal.Id.Value}'.");
        Console.WriteLine($"collaboration-dismissed={dismissed} human-input-dismiss-queued={queued} non-dismissible-skipped={requests.Length - queued}");
    }

    private static CollaborationItem? FindAttentionItemById(
        IReadOnlyList<CollaborationItem> items,
        string itemId)
    {
        var matches = items
            .Where(item => CollaborationItemLifecycle.IsReachUpType(item.Type))
            .Where(item => item.Id.StartsWith(itemId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return matches.Count switch
        {
            1 => matches[0],
            0 => null,
            _ => throw new ArgumentException(
                $"Attention item id '{itemId}' is ambiguous ({matches.Count} matches); use the full item id from `attention show`.")
        };
    }

    private static void Queue(CliExecutionContext context, IReadOnlyList<string> parts, HumanInputRequest request)
    {
        var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(
            parts, CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
        var intentId = Guid.NewGuid().ToString("N");
        var intent = new OperatorIntentRecord(
            intentId, intentId, OperatorIntentVerbs.HumanInputDismiss, request.GoalId.Value, TaskId: null,
            JsonSerializer.Serialize(new HumanInputDismissOperatorIntentPayload(request.Id.Value), OperatorIntentJson.Options),
            [], attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance,
            DateTimeOffset.UtcNow, ActorKind: attribution.ActorKind);
        var persisted = SqliteOperatorIntentStore.ForDirectories(
            context.Workspace.OrchestratorDirectory, context.Workspace.LogDirectory)
            .EnqueueAsync(intent).GetAwaiter().GetResult();
        Console.WriteLine(
            $"Operator intent queued: id={persisted.Id} verb={persisted.Verb} goal={persisted.GoalId} request={request.Id} status={persisted.Status}. " +
            $"Poll with `operator-intent-status {persisted.Id}`.");
    }
}
