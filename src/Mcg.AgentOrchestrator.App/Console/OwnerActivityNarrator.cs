using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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

    internal static bool IsLanding(OwnerConductEvent item) => item.GoalId is not null &&
        item.EventKind == "loop-relaunch" && Head(item) is "LOOP_RELAUNCH_SCHEDULED" or "LOOP_RELAUNCH_NOT_REQUIRED";

    internal static int LandedToday(IEnumerable<OwnerConductEvent> events, TimeProvider clock) => events
        .Where(IsLanding).Where(item => TimeZoneInfo.ConvertTime(item.Timestamp, clock.LocalTimeZone).Date ==
            clock.GetLocalNow().Date).Distinct().Count();

    internal static IReadOnlyList<OwnerConsoleActivityItem> Narrate(IEnumerable<OwnerConductEvent> events,
        Func<string?, string> title, Func<OwnerConductEvent, OwnerActivityTestEvidence?>? evidence = null,
        IReadOnlyList<OwnerAttentionObservation>? attention = null, Func<OwnerConductEvent, string?>? finding = null)
    {
        var ordered = events.Where(Maps).Distinct().OrderBy(item => item.Timestamp).ToArray();
        var result = new List<OwnerConsoleActivityItem>();
        var held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var landed = new HashSet<OwnerConductEvent>();
        string[] mostRecentLanding = [];
        string[] mostRecentLandingPrefixes = [];
        var lifecycle = new OwnerConductorLifecycleNarration();
        var escalations = new OwnerEscalationNarration(ordered, new OwnerQuestionRoutes(ordered), attention, Name);
        for (var index = 0; index < ordered.Length; index++)
        {
            var item = ordered[index];
            var prefix = Prefix(item.GoalId);
            var name = Name(item.GoalId);
            var head = Head(item);
            if (held.Contains(prefix) && ClearsHold(item))
            {
                held.Remove(prefix);
                Add(item, "Moving again: " + name, "The earlier hold has cleared.", "Work can continue.",
                    spans: SingleTitle(name, "Moving again: ".Length));
            }
            if (IsLanding(item))
            {
                if (!landed.Add(item)) continue;
                // Group one contiguous train; a process restart can reuse a loop number.
                var train = new List<OwnerConductEvent> { item };
                for (var next = index + 1; next < ordered.Length && IsLanding(ordered[next]) &&
                    SameTrain(item, ordered[next]); next++)
                    if (landed.Add(ordered[next])) train.Add(ordered[next]);
                var goals = train.Select(value => value.GoalId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                mostRecentLanding = goals.Select(Name).ToArray();
                mostRecentLandingPrefixes = goals.Select(Prefix).ToArray();
                var landing = GoalList(goals.Length > 1 ? "Landed together: " : "Landed: ",
                    mostRecentLanding, goals.Select(id => $" ({Prefix(id)})").ToArray());
                Add(item, landing.Text,
                    "The work passed its checks and was added to main.", "The conductor will continue with the remaining work.",
                    spans: landing.Spans, goalPrefixes: goals.Length > 1 ? mostRecentLandingPrefixes : null);
                result[^1] = result[^1] with { Evidence = train.ToArray() };
                continue;
            }
            switch (item.EventKind)
            {
                case "goal-lifecycle":
                    if (DetailPhrase(item, name, finding?.Invoke(item)) is { } stage)
                        Add(item, OwnerActivityAttempts.Label(stage, DetailLead(item)!.Length + name.Length + (head == "TaskFailed" ? " back".Length : 0),
                            OwnerActivityAttempts.Number(ordered, index)), FailureReason(item) is { } rejection ? "plan rejected: " + rejection : head == "TaskFailed" ? Field(item, "outcome") == "finding" ?
                            "The worker reported a blocking finding." : "The worker could not finish its task." :
                            "The work reached this stage.", head == "TaskFailed" ? Field(item, "outcome") == "finding" ?
                            "The Developer will address the finding." : "The conductor will retry the worker or request help." : "Work continues through the remaining checks.",
                            spans: SingleTitle(name, DetailLead(item)!.Length));
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
                        mostRecentLanding, mostRecentLandingPrefixes, out var health))
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
            var key = Prefix(source.GoalId);
            if (!held.Add(key)) return;
            var seconds = Field(source, "repeatedForSeconds") ?? Field(source, "heldForSeconds");
            var minutes = double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var duration)
                ? Math.Max(0, (int)(duration / 60)) : 0;
            var blocker = WaitingOn(Field(source, "blocker"));
            var waitingTitle = Name(source.GoalId);
            Add(source, $"Waiting: {waitingTitle} has been held {minutes} min: {blocker}",
                blocker, "Work resumes when the blocker clears.",
                "Check DECISIONS for an owner question; otherwise the conductor will retry when it can.",
                spans: SingleTitle(waitingTitle, "Waiting: ".Length));
        }
    }

    internal static OwnerConsoleActivityItem Item(OwnerConductEvent source, string fullName, IReadOnlyList<OwnerConductEvent> ordered,
        string sentence, string why, string next, string act = "No. You can let the conductor continue.",
        OwnerConsoleLineSpans? spans = null, IReadOnlyList<string>? goalPrefixes = null) =>
        new(source.Timestamp, source.EventKind, "outcome", Prefix(source.GoalId), source.Detail,
            fullName, sentence, why, next, act, why, Resolution: OwnerActivityProgress.RetryAfter(source, ordered),
            Spans: spans, GoalPrefixes: goalPrefixes, GoalId: source.GoalId, Evidence: [source]);

    private static string? DetailLead(OwnerConductEvent item) => Head(item) switch
    {
        "TaskDispatched" => Role(item) + " started on ",
        "TaskCompleted" => Role(item) + " passed ",
        "TaskFailed" => Role(item) + " sent ",
        _ => null
    };

    internal static string? DetailPhrase(OwnerConductEvent item, string title, string? finding = null) => DetailLead(item) is { } lead
        ? lead + title + (Head(item) == "TaskFailed" ? " back: " + (FailureReason(item) is { } rejected
            ? "plan rejected: " + rejected : OwnerHoldReason.FirstLine(finding) ?? (Field(item, "outcome") == "finding"
                ? "a problem needs correction" : "the worker could not finish")) : "") : null;

    private static OwnerConsoleTitleSpan TitleSpan(string title, int start, int trailingLength = 0)
    {
        var colon = title.IndexOf(": ", StringComparison.Ordinal);
        return new(start, title.Length, colon < 0 ? 0 : colon + 1, trailingLength);
    }

    internal static OwnerConsoleLineSpans SingleTitle(string title, int start) => new([TitleSpan(title, start)]);

    internal static (string Text, OwnerConsoleLineSpans Spans) GoalList(
        string lead, IReadOnlyList<string> titles, IReadOnlyList<string>? tails = null)
    {
        var text = new StringBuilder(lead);
        var spans = new List<OwnerConsoleTitleSpan>();
        for (var i = 0; i < titles.Count; i++)
        {
            if (i > 0) text.Append(", ");
            var tail = tails?[i] ?? "";
            spans.Add(TitleSpan(titles[i], text.Length, tail.Length));
            text.Append(titles[i]).Append(tail);
        }
        return (text.ToString(), new(spans.ToArray()));
    }

    internal static string? StagePhrase(OwnerConductEvent item, string? finding = null) => Head(item) switch
    {
        "TaskDispatched" => Role(item) + " started",
        "TaskCompleted" => Role(item) + " passed",
        "TaskFailed" => Role(item) + " sent back: " + (FailureReason(item) is { } reason ? "plan rejected: " + reason :
            finding ?? (Field(item, "outcome") == "finding" ? "a problem needs correction" : "the worker could not finish")),
        _ => null
    };

    internal static string Line(OwnerConsoleActivityItem item) => $"{item.Timestamp.ToLocalTime():HH:mm:ss} " +
        (item.GoalPrefix.Length > 0 && !item.Phrase.Contains(item.GoalPrefix, StringComparison.OrdinalIgnoreCase)
            ? item.GoalPrefix + " " : "") + item.Phrase;

    internal static OwnerConsoleLineSpans LineSpans(OwnerConsoleActivityItem item) =>
        (item.Spans ?? OwnerConsoleLineSpans.None).Shift(Line(item).Length - item.Phrase.Length);

    internal static string Explain(OwnerConsoleActivityItem item, IReadOnlyList<OwnerConsoleDecision>? decisions = null)
        => ExplainWithFields(item, decisions).Text;

    internal static (string Text, OwnerConsoleFieldSnapshot Fields, OwnerConsoleStructuredText Structured) ExplainWithFields(
        OwnerConsoleActivityItem item, IReadOnlyList<OwnerConsoleDecision>? decisions = null)
    {
        // Match the exact item; another question on the same goal never makes this event actionable.
        var decision = item.OwnerQuestionId is null ? null : decisions?.FirstOrDefault(value =>
            value.Id == item.OwnerQuestionId && value.GoalPrefix.Equals(item.GoalPrefix, StringComparison.OrdinalIgnoreCase) &&
            (item.GoalId is null || value.GoalId.Equals(item.GoalId, StringComparison.OrdinalIgnoreCase)) &&
            Words(OwnerHoldReason.FirstLine(value.FullText), "") == item.Subject);
        var resolution = decision is not null ? "still waiting on you" : item.Resolution is { } resolved ?
            resolved.AutomaticRetry ? $"retried automatically at {resolved.At.ToLocalTime():HH:mm:ss}" :
                $"resolved at {resolved.At.ToLocalTime():HH:mm:ss}" + (resolved.Actor is { } actor ? " by " + ResolutionActor(actor) :
                    "; no longer listed in DECISIONS (the resolving actor was not recorded)") :
            item.OwnerQuestionId is not null ? "no longer listed in DECISIONS" : "no owner question is listed in DECISIONS for this event";
        var act = decision is not null ? $"Yes. Open DECISIONS row [{decision.Number}] {decision.GoalPrefix} {decision.Kind}: {decision.Summary}" :
            $"No. {char.ToUpperInvariant(resolution[0])}{resolution[1..]}.";
        var next = decision is not null ? "Work waits for your answer." : item.Resolution?.AutomaticRetry == true ?
            "The conductor resumed this work." : item.OwnerQuestionId is not null ?
            "This question no longer requires an owner answer." : item.Next;
        var why = item.Subject ?? item.Why;
        var nextStep = (decision is not null || item.Resolution is not null || item.OwnerQuestionId is not null ? resolution + ". " : "") + next;
        return OwnerActivityDetailPresentation.Build(item, why, nextStep, act);
    }

    internal static string? FailureReason(OwnerConductEvent item)
    {
        var reason = Field(item, "rejection") ?? OwnerPlanRejectionReason.Read(item.Detail);
        if (reason is null) return null;
        var citation = Regex.Match(reason, @"target citation '([^']+)' does not exist");
        return citation.Success ? "cited a file that does not exist: " + citation.Groups[1].Value : reason;
    }

    private static string ResolutionActor(string actor) => actor.ToLowerInvariant() switch
    {
        "author" => "the Author",
        "conductor" => "the conductor",
        "operator" or "owner" or "owner-console" or "miles" => "the operator",
        _ => actor
    };

    private static bool SameTrain(OwnerConductEvent left, OwnerConductEvent right) => Field(left, "tick") is { } value
        ? value == Field(right, "tick") : left.Timestamp == right.Timestamp;
    private static bool ClearsHold(OwnerConductEvent item) => item.EventKind is "owner-hold-cleared" or "train-receipt-released" ||
        item.EventKind == "goal-lifecycle" && Head(item) is "TaskDispatched" or "TaskCompleted" || IsLanding(item);
    internal static string Role(OwnerConductEvent item) => Field(item, "role") is "Author" or "Planner" or "Researcher" or "Developer" or "Tester" or "Reviewer"
        ? Field(item, "role")! : "Worker";
}

internal sealed record OwnerActivityTestEvidence(IReadOnlyList<string> Tests, IReadOnlyList<string> Checks, bool OwnTest = false,
    string? FirstFailure = null);
