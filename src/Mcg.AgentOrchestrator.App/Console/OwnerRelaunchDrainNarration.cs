using System.Globalization;
using Mcg.AgentOrchestrator.App.Cli;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerEventFields;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerActivityNarrator;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Each scheduled window owns one latest valid sample within this event snapshot.
internal sealed class OwnerRelaunchDrainNarration
{
    private readonly IReadOnlyList<OwnerConductEvent> ordered;
    private readonly Func<string?, string> name;
    private readonly Dictionary<int, DrainWindow> latest = [];

    internal OwnerRelaunchDrainNarration(IReadOnlyList<OwnerConductEvent> ordered, Func<string?, string> name)
    {
        this.ordered = ordered;
        this.name = name;
        var open = new Dictionary<string, DrainWindow>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < ordered.Count; index++)
        {
            var item = ordered[index];
            var head = Head(item);
            if (item.EventKind == "loop-relaunch" && item.GoalId is { } goal)
            {
                if (head == "LOOP_RELAUNCH_SCHEDULED") open[goal] = new(item);
                else if (head == "LOOP_RELAUNCH_DRAIN" && open.TryGetValue(goal, out var window) &&
                    Count(item, "active", out var workers) && Count(item, "liveGates", out var gates))
                {
                    latest.Remove(window.LatestIndex);
                    window.LatestIndex = index;
                    window.Workers = workers;
                    window.Gates = gates;
                    window.Drains.Add(item);
                    latest[index] = window;
                }
            }

            var goalTerminal = (item.EventKind is "loop-relaunch" or "loop-relaunch-rollback") &&
                (head is "LOOP_RELAUNCH_REBUILD" or "LOOP_RELAUNCH_ROLLBACK");
            var conductorTerminal = (item.EventKind == "loop-handoff" && head is
                "LOOP_HANDOFF" or "LOOP_HANDOFF_FAILED" or "ACTIVATION_ADOPTED" or "ACTIVATION_REVERTED" or "ACTIVATION_FAILED_BOTH") ||
                (item.EventKind == "loop-stop" && Field(item, "reason") == "self-relaunch-handoff");
            if (goalTerminal || conductorTerminal)
            {
                if (item.GoalId is { } closedGoal) open.Remove(closedGoal);
                else if (conductorTerminal) open.Clear();
            }
        }
    }

    internal OwnerConsoleActivityItem? NarrateItem(int index)
    {
        if (!latest.TryGetValue(index, out var window)) return null;
        var drain = ordered[index];
        var title = name(window.Scheduled.GoalId);
        var reason = Field(drain, "reason") is { Length: > 0 } value ? $" (reason: {value})" : "";
        var elapsed = ConsoleDurationFormat.Format(drain.Timestamp - window.Scheduled.Timestamp);
        var sentence = $"Restart waiting for {window.Workers} running {(window.Workers == 1 ? "worker" : "workers")} " +
            $"and {window.Gates} live {(window.Gates == 1 ? "gate" : "gates")}{reason}; elapsed {elapsed}: {title}";
        return Item(drain, title, ordered, sentence,
            "The conductor is waiting for running work to finish before restarting.",
            "The conductor will restart when the wait clears.") with { Evidence = [window.Scheduled, ..window.Drains] };
    }

    private static bool Count(OwnerConductEvent item, string field, out int count) =>
        int.TryParse(Field(item, field), NumberStyles.None, CultureInfo.InvariantCulture, out count);

    private sealed class DrainWindow(OwnerConductEvent scheduled)
    {
        internal OwnerConductEvent Scheduled { get; } = scheduled;
        internal List<OwnerConductEvent> Drains { get; } = [];
        internal int LatestIndex { get; set; } = -1;
        internal int Workers { get; set; }
        internal int Gates { get; set; }
    }
}
