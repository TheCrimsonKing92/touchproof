using System.Text;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityNarrator;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityLinePresentation;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// One chronological fold owns its consumed landing events and latest train.
internal sealed class OwnerLandingNarration(IReadOnlyList<OwnerConductEvent> ordered, Func<string?, string> name)
{
    private readonly HashSet<OwnerConductEvent> landed = [];
    internal string[] MostRecentLanding { get; private set; } = [];
    internal string[] MostRecentLandingPrefixes { get; private set; } = [];

    internal static bool IsLanding(OwnerConductEvent item) => item.GoalId is not null &&
        item.EventKind == "loop-relaunch" && Head(item) is "LOOP_RELAUNCH_SCHEDULED" or "LOOP_RELAUNCH_NOT_REQUIRED";

    internal OwnerConsoleActivityItem? Narrate(int index)
    {
        var item = ordered[index];
        if (!landed.Add(item)) return null;
        // Group one contiguous train; a restart can reuse a loop number.
        var train = new List<OwnerConductEvent> { item };
        for (var next = index + 1; next < ordered.Count && IsLanding(ordered[next]) &&
            SameTrain(item, ordered[next]); next++)
            if (landed.Add(ordered[next])) train.Add(ordered[next]);
        var goals = train.Select(value => value.GoalId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        MostRecentLanding = goals.Select(name).ToArray();
        MostRecentLandingPrefixes = goals.Select(Prefix).ToArray();
        var landing = GoalList(goals.Length > 1 ? "Landed together: " : "Landed: ",
            MostRecentLanding, goals.Select(id => $" ({Prefix(id)})").ToArray());
        return Item(item, name(item.GoalId), ordered, landing.Text,
            "The work passed its checks and was added to main.", "The conductor will continue with the remaining work.",
            spans: landing.Spans, goalPrefixes: goals.Length > 1 ? MostRecentLandingPrefixes : null)
            with { Evidence = train.ToArray() };
    }

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

    private static bool SameTrain(OwnerConductEvent left, OwnerConductEvent right) => Field(left, "tick") is { } value
        ? value == Field(right, "tick") : left.Timestamp == right.Timestamp;
}
