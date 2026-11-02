using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns visibility of an existing evidence hold; admission and closure retain their decisions.
internal sealed class StalledEvidenceWaitAttention(
    ICollaborationItemStore store, Func<Goal, IReadOnlyList<HumanInputRequest>?> pending)
{
    internal const string Prefix = "stalled-evidence-wait:";

    internal void ReconcileAll(IEnumerable<Goal> goals)
    {
        var open = store.GetAttentionQueueAsync().GetAwaiter().GetResult()
            .Where(item => !CollaborationItemLifecycle.IsTerminal(item.Status) &&
                item.CorrelationKey?.StartsWith(Prefix, StringComparison.Ordinal) == true)
            .Select(item => item.GoalId).ToHashSet(StringComparer.Ordinal);
        foreach (var goal in goals.Where(goal => goal.Status == GoalStatus.Verified || open.Contains(goal.Id.Value)))
            PassedGateHoldFaultIsolation.Run(goal, "reconcile-stalled-evidence-wait-attention", () => Reconcile(goal));
    }

    internal void Reconcile(Goal goal)
    {
        // An unavailable kernel is not evidence that the hold cleared.
        if (pending(goal) is { } requests) Reconcile(goal, requests);
    }

    internal void Reconcile(Goal goal, IReadOnlyList<HumanInputRequest> requests) => Reconcile(store, goal, requests);

    internal static void Reconcile(ICollaborationItemStore store, Goal goal, IReadOnlyList<HumanInputRequest> requests)
    {
        var key = Prefix + goal.Id.Value;
        var stalled = FindStall(goal, requests);
        if (stalled is null)
            store.TryResolveAsync(key, "Stalled evidence wait cleared.").GetAwaiter().GetResult();
        else
            store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value,
                $"evidence wait open after {stalled.Value.Owner} completed",
                BuildBody(goal, stalled.Value.Wait, stalled.Value.Owner, stalled.Value.Task), key).GetAwaiter().GetResult();
    }

    private static (HumanInputRequest Wait, string Owner, TaskSpec Task)? FindStall(
        Goal goal, IReadOnlyList<HumanInputRequest> requests)
    {
        if (goal.Status != GoalStatus.Verified ||
            GroupedAdmissionLandingHold.FindBlockingWait(requests, goal) is not { } wait ||
            wait.Kind != HumanWaitKind.ProspectiveAcceptanceEvidence ||
            !string.Equals(wait.PlannerEvidence?.Availability, "post-implementation", StringComparison.OrdinalIgnoreCase))
            return null;
        var owner = wait.EvidenceOwner?.Trim().ToLowerInvariant();
        if (owner is not ("developer" or "tester" or "reviewer")) return null;
        var role = owner switch
        {
            "developer" => AgentRole.Developer,
            "tester" => AgentRole.Tester,
            _ => AgentRole.Reviewer
        };
        var task = goal.Tasks.Where(task => task.RequiredRole == role && task.LastDispatch is not null)
            .MaxBy(task => task.LastDispatch!.DispatchedAt);
        return task?.Status == WorkTaskStatus.Completed ? (wait, owner, task) : null;
    }

    private static string BuildBody(Goal goal, HumanInputRequest wait, string owner, TaskSpec task)
    {
        var requestPrefix = ShortId(wait.Id.Value);
        var note = goal.Timeline.LastOrDefault(entry => entry.Kind == ProgressKind.TaskNote &&
            (entry.Message.StartsWith($"REVIEWER_VERDICT_EVIDENCE_SKIPPED request={requestPrefix} ", StringComparison.Ordinal) ||
             entry.Message.StartsWith($"ROLE_REPORT_EVIDENCE_SKIPPED request={requestPrefix} ", StringComparison.Ordinal)));
        return string.Join(Environment.NewLine,
            $"Wait: {wait.Id.Value}",
            $"Owner: {owner}",
            $"Owner task: {task.Id.Value}",
            $"Last closure attempt: {note?.Message ?? "no closure attempt recorded"}",
            $"Answer: attention answer {ShortId(goal.Id.Value)} {requestPrefix} --text-file <path>");
    }

    private static string ShortId(string value) => value[..Math.Min(8, value.Length)];
}
