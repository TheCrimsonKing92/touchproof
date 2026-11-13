using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityText;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityEventMap;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityLinePresentation;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A pure chronological fold: the sentence and its explanation are one owner outcome.
internal static class OwnerActivityNarrator
{
    internal static int LandedToday(IEnumerable<OwnerConductEvent> events, TimeProvider clock) => events
        .Where(OwnerLandingNarration.IsLanding).Where(item => TimeZoneInfo.ConvertTime(item.Timestamp, clock.LocalTimeZone).Date ==
            clock.GetLocalNow().Date).Distinct().Count();

    internal static IReadOnlyList<OwnerConsoleActivityItem> Narrate(IEnumerable<OwnerConductEvent> events,
        Func<string?, string> title, Func<OwnerConductEvent, OwnerActivityTestEvidence?>? evidence = null,
        IReadOnlyList<OwnerAttentionObservation>? attention = null, Func<OwnerConductEvent, string?>? finding = null)
    {
        var ordered = events.Where(Maps).Distinct().OrderBy(item => item.Timestamp).ToArray();
        var result = new List<OwnerConsoleActivityItem>();
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var landings = new OwnerLandingNarration(ordered, Name);
        var lifecycle = new OwnerConductorLifecycleNarration();
        var escalations = new OwnerEscalationNarration(ordered, new OwnerQuestionRoutes(ordered), attention, Name);
        for (var index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            var prefix = Prefix(item.GoalId);
            var name = Name(item.GoalId);
            if (held.Contains(prefix) && ClearsHold(item))
            {
                held.Remove(prefix);
                result.Add(Item(item, Name(item.GoalId), ordered, "Moving again: " + name,
                    "The earlier hold has cleared.", "Work can continue.", spans: SingleTitle(name, "Moving again: ".Length)));
            }
            if (OwnerLandingNarration.IsLanding(item))
            {
                if (landings.Narrate(index) is { } landing) result.Add(landing);
                continue;
            }
            var narrated = item.EventKind switch
            {
                "goal-lifecycle" => OwnerLifecycleStageNarration.NarrateItem(ordered, index, name, Name, finding?.Invoke(item)),
                "owner-question-resolved" => null, // Only removal from the live read model resolves owner attention.
                "author" or "goal-escalation" => escalations.NarrateItem(index, held),
                "goal-stalled" => OwnerStallNarration.NarrateItem(ordered, index, held, Name),
                "test-impact-headroom-low" => OwnerTestImpactHeadroomNarration.NarrateItem(ordered, index, Name),
                "provider-hold-placed" or "provider-hold-probe" or "provider-hold-restored" or "provider-hold-overdue" or "provider-hold-check" =>
                    OwnerProviderHoldNarration.NarrateItem(ordered, index, Name),
                "experiment-reading-due" => OwnerExperimentReadingNarration.NarrateItem(ordered, index, Name),
                "acceptance" or "acceptance-cohort" => OwnerAcceptanceNarration.NarrateItem(ordered, index, Name, evidence, attention),
                _ => lifecycle.NarrateItem(ordered, index, Name) ?? OwnerMainHealthNarration.NarrateItem(ordered, index, Name,
                    evidence, landings.MostRecentLanding, landings.MostRecentLandingPrefixes)
            };
            if (narrated is not null) result.Add(narrated);
        }
        result.AddRange(new OwnerAttentionNarration(ordered, Name).Narrate(attention));
        return result.OrderByDescending(item => item.Timestamp).ThenByDescending(item => item.Kind == "owner-question-resolved")
            .Take(OwnerConsoleViewModelBuilder.MaxActivityItems).ToArray();

        string Name(string? id) => Words(title(id), Prefix(id).Length == 0 ? "This work" : Prefix(id));
    }

    internal static OwnerConsoleActivityItem Item(OwnerConductEvent source, string fullName, IReadOnlyList<OwnerConductEvent> ordered,
        string sentence, string why, string next, string act = "No. You can let the conductor continue.",
        OwnerConsoleLineSpans? spans = null, IReadOnlyList<string>? goalPrefixes = null) =>
        new(source.Timestamp, source.EventKind, "outcome", Prefix(source.GoalId), source.Detail,
            fullName, sentence, why, next, act, why, Resolution: OwnerActivityProgress.RetryAfter(source, ordered),
            Spans: spans, GoalPrefixes: goalPrefixes, GoalId: source.GoalId, Evidence: [source]);

    private static bool ClearsHold(OwnerConductEvent item) => item.EventKind is "owner-hold-cleared" or "train-receipt-released" ||
        item.EventKind == "goal-lifecycle" && Head(item) is "TaskDispatched" or "TaskCompleted" || OwnerLandingNarration.IsLanding(item);
    internal static string Role(OwnerConductEvent item) => Field(item, "role") is "Author" or "Planner" or "Researcher" or "Developer" or "Tester" or "Reviewer"
        ? Field(item, "role")! : "Worker";
}

internal sealed record OwnerActivityTestEvidence(IReadOnlyList<string> Tests, IReadOnlyList<string> Checks, bool OwnTest = false,
    string? FirstFailure = null);
