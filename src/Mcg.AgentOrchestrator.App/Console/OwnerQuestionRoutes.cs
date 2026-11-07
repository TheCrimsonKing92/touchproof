using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerQuestionRoutes
{
    private readonly Dictionary<OwnerConductEvent, OwnerConductEvent> questionRoutes = new();
    private readonly HashSet<OwnerConductEvent> foldedAuthors = new();

    internal OwnerQuestionRoutes(IReadOnlyList<OwnerConductEvent> ordered)
    {
        foreach (var observation in ordered.Where(item => item.EventKind == "goal-escalation" && Head(item) == "author-owner-question"))
        {
            var candidates = ordered.Where(item => item.EventKind == "author" && !foldedAuthors.Contains(item) &&
                Prefix(item.GoalId) == Prefix(observation.GoalId) && SameQuestion(item, observation)).ToArray();
            var route = candidates.Where(item => item.Timestamp <= observation.Timestamp).MaxBy(item => item.Timestamp) ??
                candidates.MinBy(item => item.Timestamp);
            if (route is null) continue;
            questionRoutes[observation] = route;
            foldedAuthors.Add(route);
        }
    }

    internal OwnerConductEvent? Route(OwnerConductEvent escalation) => questionRoutes.GetValueOrDefault(escalation);
    internal bool Folded(OwnerConductEvent item) => foldedAuthors.Contains(item);

    private static bool SameQuestion(OwnerConductEvent author, OwnerConductEvent observation)
    {
        var left = Field(author, "question-id") ?? Field(author, "item");
        var right = Field(observation, "question-id") ?? Field(observation, "item");
        // Author item identities include a goal prefix; question observations omit that prefix.
        if (left is not null && right is not null)
            return left.Equals(right, StringComparison.OrdinalIgnoreCase) ||
                left.Equals(author.GoalId + ":" + right, StringComparison.OrdinalIgnoreCase);
        var agent = Field(author, "agent");
        var observedAgent = Field(observation, "agent");
        return (agent is null || observedAgent is null || agent.Equals(observedAgent, StringComparison.OrdinalIgnoreCase)) &&
            author.Timestamp <= observation.Timestamp;
    }
}
