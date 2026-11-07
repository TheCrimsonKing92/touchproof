using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed record OwnerGoalDetailSnapshot(
    Goal Goal, int? LandingQueuePosition, IReadOnlyList<OwnerConductEvent> RecentEvents,
    OwnerConsoleGateProgress? Gate = null, bool GateObserved = false, OwnerConsoleGateEnd? LastGateEnd = null,
    string? PriorityWarning = null, OwnerGoalChangeSummary? Changes = null,
    OwnerConsoleEvidenceAttemptProgress? Evidence = null)
{
    internal OwnerConsoleFieldSnapshot Fields()
    {
        var goal = Goal;
        var fields = new List<OwnerConsoleField>
        {
            new("goal", "Goal ID", goal.Id.Value), new("title", "Title", OwnerGoalTitle.From(goal.Objective)),
            new("objective", "Objective", goal.Objective), new("status", "Status", OwnerConsoleStatusLabel.For(goal.Status)),
            new("stage", "Stage", GoalLifecycle.ResolveState(goal, new(IsBlocked: goal.CurrentHold is not null)).ToString()),
            new("role", "Role", OwnerConsoleGoalDetail.Stage(goal)), new("priority", "Priority", GoalEffectivePriorityIndex.StoredLevel(goal).ToString()),
            new("priority/own", "Own priority", goal.Priority.ToString()),
            new("priority/inherited-from", "Priority inherited from goal ID", goal.PriorityScheduling?.InheritedFromGoalId),
            new("isolation", "Isolation", goal.Isolation.ToString()), new("backlog", "Source backlog ID", goal.SourceBacklogItemId),
            new("coverage", "Source coverage", goal.SourceBacklogCoverage?.ToString()),
            new("queue", "Landing queue position", LandingQueuePosition?.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("warning", "Priority warning", PriorityWarning)
        };
        fields.Add(new("waiting", "Waiting on", goal.CurrentHold is { } hold ? OwnerActivityText.WaitingOn(hold.Blocker) : null));
        if (goal.LandingBlocker is { } blocker)
            fields.AddRange(new OwnerConsoleField[] {
                new("landing-blocker/kind", "Landing blocker kind", blocker.Kind),
                new("landing-blocker/since", "Landing blocker since", blocker.Since.ToString("O")),
                new("landing-blocker/next-actor", "Landing blocker next actor", blocker.NextActor),
                new("landing-blocker/next-verb", "Landing blocker next verb", blocker.NextVerb),
                new("landing-blocker/detail", "Landing blocker detail", blocker.Detail) });
        fields.Add(new("last-gate/at", "Last gate at", LastGateEnd?.At.ToString("O")));
        fields.Add(new("last-gate/result", "Last gate result", LastGateEnd?.Result));
        foreach (var task in goal.Tasks)
        {
            var key = "task/" + task.Id.Value + "/";
            fields.AddRange(new OwnerConsoleField[] { new(key + "id", task.RequiredRole + " task ID", task.Id.Value),
                new(key + "description", task.RequiredRole + " description", task.Description),
                new(key + "status", task.RequiredRole + " status", task.Status.ToString()) });
        }
        if (Changes is { } changes) fields.AddRange(changes.Fields());
        fields.AddRange(OwnerGoalReturnExplanation.Fields(goal));
        for (var i = 0; i < RecentEvents.Count; i++)
        {
            var item = RecentEvents[i];
            fields.AddRange(new OwnerConsoleField[] { new("event/" + i + "/at", "Recent event at", item.Timestamp.ToString("O")),
                new("event/" + i + "/kind", "Recent event kind", item.EventKind),
                new("event/" + i + "/detail", "Recent event detail", item.Detail),
                new("event/" + i + "/phrase", "Recent event", OwnerLifecycleStageNarration.StagePhrase(item, OwnerConsoleGoalDetailFormatter.Finding(goal, item))) });
        }
        if (Gate is { } gate) fields.AddRange(gate.FieldSnapshot().Fields.Select(field => field with { Key = "gate/" + field.Key }));
        if (Evidence is { } evidence)
        {
            fields.Add(new("evidence/start", "Evidence started", evidence.StartedAt?.ToString("O")));
            fields.Add(new("evidence/ordinal", "Evidence attempt", evidence.Ordinal?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
            fields.Add(new("evidence/observed", "Evidence observed at", evidence.ObservedAt.ToString("O")));
            fields.Add(new("evidence/elapsed", "Evidence elapsed", evidence.StartedAt is { } start && start <= evidence.ObservedAt ? (evidence.ObservedAt - start).ToString("c") : null));
            fields.Add(new("evidence/lane", "Evidence lane", evidence.Lane));
            fields.Add(new("evidence/passed", "Evidence passed", evidence.Passed?.ToString()));
            fields.Add(new("evidence/failed", "Evidence failed", evidence.Failed?.ToString()));
            fields.Add(new("evidence/updated", "Evidence updated", evidence.LastProgressAt?.ToString("O")));
            fields.Add(new("evidence/previous/duration", "Last attempt duration seconds", evidence.Previous?.DurationSeconds?.ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            fields.Add(new("evidence/previous/outcome", "Last attempt outcome", evidence.Previous?.Outcome));
            fields.Add(new("evidence/previous/tests", "Last attempt tests", evidence.Previous?.TestsExecuted?.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        }
        return new(goal.Id.Value, [.. fields]);
    }

    internal OwnerGoalDetailSnapshot WithGate(OwnerConsoleGateProgress? observed) => this with
    {
        Gate = observed?.ForMember(Goal.Id.Value),
        GateObserved = observed is not null,
        LastGateEnd = OwnerConsoleGateEnd.LatestFor(observed?.Ended, Goal.Id.Value)
    };
}
