using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityText;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// A pure chronological fold: the sentence and its explanation are one owner outcome.
internal static class OwnerActivityNarrator
{
    internal const string JumpKeyHint = "Home/End first/last  PgUp/PgDn page";
    internal static IReadOnlyList<string> JumpKeyHelp => [
        "Home/End: Select the first/last row in DECISIONS, BOARD or ACTIVITY.",
        "PgUp/PgDn: Move the selection one visible page up/down in DECISIONS, BOARD or ACTIVITY."];

    internal static bool Maps(OwnerConductEvent item) => item.EventKind switch
    {
        "goal-lifecycle" => Head(item) is "TaskDispatched" or "TaskCompleted" or "TaskFailed" or
            "HumanInputReceived" or "HumanInputSuperseded" || Field(item, "resolution-verb") is not null,
        "state-log-divergence" => Positive(item, "lost") || Positive(item, "repeated"),
        "author" => Field(item, "kind") == "ask-owner",
        "program-health" or "acceptance" or "acceptance-cohort" or "canary-gate" or "loop-relaunch" or "loop-handoff" or
            "loop-start" or "loop-stop" or "goal-escalation" or "goal-stalled" or "train-receipt-released" or
            "owner-question-resolved" or "owner-hold-cleared" or "admission" or "infrastructure-deferral" or "experiment-reading-due" or "test-impact-headroom-low" or "provider-hold-placed" or "provider-hold-probe" or "provider-hold-restored" or "provider-hold-overdue" or "provider-hold-check" => true,
        _ => false
    };

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
                Add(item, "Moving again: " + name, "The earlier hold has cleared.", "Work can continue.",
                    spans: SingleTitle(name, "Moving again: ".Length));
            }
            if (OwnerLandingNarration.IsLanding(item))
            {
                if (landings.Narrate(index) is { } landing) result.Add(landing);
                continue;
            }
            switch (item.EventKind)
            {
                case "goal-lifecycle":
                    if (OwnerLifecycleStageNarration.TryNarrate(ordered, index, name, finding?.Invoke(item), out var stage))
                        Add(item, stage.Sentence, stage.Why, stage.Next, stage.Act, stage.Spans);
                    break;
                case "owner-question-resolved": break; // Only removal from the live read model resolves owner attention.
                case "author":
                case "goal-escalation":
                    if (escalations.Narrate(index, held) is not { } escalation) break;
                    if (escalation.Stall) { Stall(item); break; }
                    Add(item, escalation.Sentence, escalation.Why, escalation.Next, escalation.Act, escalation.Spans);
                    if (escalation.Resolution is { } resolution) result[^1] = result[^1] with { Resolution = resolution };
                    break;
                case "goal-stalled": Stall(item); break;
                case "test-impact-headroom-low":
                    if (OwnerTestImpactHeadroomNarration.TryNarrate(item, out var headroomSentence, out var headroomWhy, out var headroomNext, out var headroomAct)) Add(item, headroomSentence, headroomWhy, headroomNext, headroomAct);
                    break;
                case "provider-hold-placed" or "provider-hold-probe" or "provider-hold-restored" or "provider-hold-overdue" or "provider-hold-check":
                    if (OwnerProviderHoldNarration.TryNarrate(item, out var holdSentence, out var holdWhy, out var holdNext, out var holdAct))
                        Add(item, holdSentence, holdWhy, holdNext, holdAct);
                    break;
                case "experiment-reading-due":
                    if (OwnerExperimentReadingNarration.TryNarrate(item, out var readingSentence, out var readingWhy, out var readingNext, out var readingAct))
                        Add(item, readingSentence, readingWhy, readingNext, readingAct);
                    break;
                case "acceptance" or "acceptance-cohort":
                    if (OwnerAcceptanceNarration.TryNarrate(ordered, index, Name, evidence, attention, out var accepted))
                        Add(item, accepted.Sentence, accepted.Why, accepted.Next, accepted.Act, accepted.Spans, accepted.GoalPrefixes);
                    break;
                default:
                    if (lifecycle.TryNarrate(ordered, index, out var sentence, out var why, out var nextStep, out var act))
                        Add(item, sentence, why, nextStep, act);
                    else if (OwnerMainHealthNarration.TryNarrate(item, Name, evidence,
                        landings.MostRecentLanding, landings.MostRecentLandingPrefixes, out var health))
                        Add(item, health.Sentence, health.Why, health.Next, health.Act, health.Spans, health.GoalPrefixes);
                    break;
            }
        }
        result.AddRange(new OwnerAttentionNarration(ordered, FullName).Narrate(attention));
        return result.OrderByDescending(item => item.Timestamp).ThenByDescending(item => item.Kind == "owner-question-resolved")
            .Take(OwnerConsoleViewModelBuilder.MaxActivityItems).ToArray();

        string FullName(string? id) => Words(title(id), Prefix(id).Length == 0 ? "This work" : Prefix(id));
        string Name(string? id) => FullName(id);
        void Add(OwnerConductEvent source, string sentence, string why, string next, string act = "No. You can let the conductor continue.",
            OwnerConsoleLineSpans? spans = null, IReadOnlyList<string>? goalPrefixes = null) =>
            result.Add(Item(source, FullName(source.GoalId), ordered, sentence, why, next, act, spans, goalPrefixes));
        void Stall(OwnerConductEvent source)
        {
            if (OwnerStallNarration.TryNarrate(source, held, Name, out var stall))
                Add(source, stall.Sentence, stall.Why, stall.Next, stall.Act, stall.Spans);
        }
    }

    internal static OwnerConsoleActivityItem Item(OwnerConductEvent source, string fullName, IReadOnlyList<OwnerConductEvent> ordered,
        string sentence, string why, string next, string act = "No. You can let the conductor continue.",
        OwnerConsoleLineSpans? spans = null, IReadOnlyList<string>? goalPrefixes = null) =>
        new(source.Timestamp, source.EventKind, "outcome", Prefix(source.GoalId), source.Detail,
            fullName, sentence, why, next, act, why, Resolution: OwnerActivityProgress.RetryAfter(source, ordered),
            Spans: spans, GoalPrefixes: goalPrefixes, GoalId: source.GoalId, Evidence: [source]);

    internal static OwnerConsoleTitleSpan TitleSpan(string title, int start, int trailingLength = 0)
    {
        var colon = title.IndexOf(": ", StringComparison.Ordinal);
        return new(start, title.Length, colon < 0 ? 0 : colon + 1, trailingLength);
    }

    internal static OwnerConsoleLineSpans SingleTitle(string title, int start) => new([TitleSpan(title, start)]);

    internal static string Line(OwnerConsoleActivityItem item) => $"{item.Timestamp.ToLocalTime():HH:mm:ss} " +
        (item.GoalPrefix.Length > 0 && !item.Phrase.Contains(item.GoalPrefix, StringComparison.OrdinalIgnoreCase)
            ? item.GoalPrefix + " " : "") + item.Phrase;

    internal static OwnerConsoleLineSpans LineSpans(OwnerConsoleActivityItem item) =>
        (item.Spans ?? OwnerConsoleLineSpans.None).Shift(Line(item).Length - item.Phrase.Length);

    private static bool ClearsHold(OwnerConductEvent item) => item.EventKind is "owner-hold-cleared" or "train-receipt-released" ||
        item.EventKind == "goal-lifecycle" && Head(item) is "TaskDispatched" or "TaskCompleted" || OwnerLandingNarration.IsLanding(item);
    internal static string Role(OwnerConductEvent item) => Field(item, "role") is "Author" or "Planner" or "Researcher" or "Developer" or "Tester" or "Reviewer"
        ? Field(item, "role")! : "Worker";
}

internal sealed record OwnerActivityTestEvidence(IReadOnlyList<string> Tests, IReadOnlyList<string> Checks, bool OwnTest = false,
    string? FirstFailure = null);
