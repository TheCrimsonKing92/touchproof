using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Presentation only: the reader and next-step projection own all plan state.
internal static class OwnerConsoleEpicPlanFormatter
{
    internal static OwnerConsoleField[] Fields(Mcg.AgentOrchestrator.App.ReadModels.EpicPlanView? view)
    {
        if (view is null) return [new("plan/bar", "Plan bar", null)];
        var fields = new List<OwnerConsoleField> { new("plan/bar", "Plan bar", view.Plan.Bar),
            new("plan/outcome", "Outcome", view.OutcomeProgress.Render()),
            new("plan/next", "Next step", EpicPlanNextStep.Compute(view.Items).Text),
            new("plan/outcome-next", "Outcome next step", EpicPlanNextStep.Compute(view).Text) };
        foreach (var item in view.Items)
        {
            var key = "plan/" + item.Item.Position + "/";
            fields.AddRange(new OwnerConsoleField[] { new(key + "subject", "Plan item " + item.Item.Position + " subject", item.Subject),
                new(key + "status", "Plan item " + item.Item.Position + " status", item.Status?.ToString()),
                new(key + "detail", "Plan item " + item.Item.Position + " detail", item.Detail),
                new(key + "backlog", "Plan item backlog ID", item.Item.BacklogItemId), new(key + "text", "Plan item text", item.Item.Text) });
            if (item.SourceFacts is { } facts)
            {
                fields.AddRange(new OwnerConsoleField[] { new(key + "source-status", "Source status", facts.SourceStatus.ToString()),
                    new(key + "owner", "Owner goal ID", facts.OwnerGoalId), new(key + "coverage", "Coverage", facts.Coverage?.ToString()),
                    new(key + "ownership", "Ownership", facts.Resolution.ToString()) });
                foreach (var goal in facts.LinkedGoals.Concat(facts.DeliveredGoals).DistinctBy(goal => goal.Id))
                    fields.AddRange(new OwnerConsoleField[] { new(key + goal.Id + "/id", "Linked goal ID", goal.Id),
                        new(key + goal.Id + "/status", "Linked goal status", goal.Status), new(key + goal.Id + "/coverage", "Linked goal coverage", goal.Coverage) });
            }
        }
        foreach (var decision in view.Plan.Decisions)
        {
            var key = "decision/" + decision.Id + "/";
            fields.AddRange(new OwnerConsoleField[] { new(key + "text", "Decision", decision.Text), new(key + "author", "Decided by", decision.DecidedBy),
                new(key + "at", "Decided at", decision.DecidedAt.ToString("O")) });
        }
        foreach (var member in view.UnplannedMembers) fields.Add(new("unplanned/" + member.MemberId, "Unplanned member ID", member.MemberId));
        return fields.ToArray();
    }
    internal static IReadOnlyList<string> Lines(OwnerConsoleEpicViewModel.Detail detail, int width)
    {
        var view = detail.Plan;
        var items = view?.Items ?? [];
        var next = EpicPlanNextStep.Compute(items);
        var lines = new List<string>();
        if (next.IsStalled) lines.Add(EpicPlanNextStep.StalledText);
        lines.Add($"Bar: {view?.Plan.Bar ?? "(no bar)"}");
        lines.Add((view?.OutcomeProgress ?? EpicOutcomeProjection.Unevaluated).Render());
        if (items.Count == 0) lines.Add("(no plan)");
        foreach (var item in items.OrderBy(item => item.Item.Position))
        {
            lines.Add($"{item.Item.Position}. {item.Subject} — {item.Render()}");
            if (item.SourceFacts is { } facts) lines.Add($"  {facts.Render()}");
        }

        lines.Add("Decisions (newest first):");
        var decisions = view?.Plan.Decisions ?? [];
        if (decisions.Count == 0) lines.Add("  (none)");
        foreach (var decision in decisions.OrderByDescending(decision => decision.DecidedAt)
                     .ThenByDescending(decision => decision.Id).Take(5))
        {
            var author = string.IsNullOrWhiteSpace(decision.DecidedBy) ? "" : $" by {decision.DecidedBy}";
            lines.Add($"  {decision.DecidedAt.ToLocalTime():yyyy-MM-dd HH:mm}{author}: {decision.Text}");
        }
        if (decisions.Count > 5)
            lines.Add($"  {decisions.Count - 5} older decisions: see epic-plan {Short(detail.Epic.Id)}");
        if (items.Count > 0 && !next.IsStalled) lines.Add($"Next step: {next.Text}");
        if (view is not null) lines.Add($"Outcome next step: {EpicPlanNextStep.Compute(view).Text}");

        lines.Add("Not in plan:");
        var unplanned = view?.UnplannedMembers ?? [];
        if (unplanned.Count == 0) lines.Add("  (none)");
        foreach (var member in unplanned)
            lines.Add($"  {(member.Kind == PortfolioMemberKind.Goal ? "goal" : "backlog item")} {Short(member.MemberId)}");
        return lines.SelectMany(line => OwnerConsoleEpicFormatter.Wrap(line, width)).ToArray();
    }

    private static string Short(string id) => id[..Math.Min(8, id.Length)];
}
