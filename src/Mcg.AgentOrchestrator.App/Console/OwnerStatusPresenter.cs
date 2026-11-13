using System.Text;
using Terminal.Gui.Text;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerStatusPresenter
{
    private ConductorLiveness _lastStableLiveness = ConductorLiveness.NotRunning;

    internal string Compose(OwnerConsoleStatus status, OwnerConsoleGateProgress? gate, string bellText,
        DateTimeOffset? refreshedAt, DateTimeOffset now, TimeZoneInfo localZone,
        TimeSpan refreshInterval, IReadOnlyCollection<string> working, int width = 0)
    {
        if (status.ConductorRunning != ConductorLiveness.Unknown) _lastStableLiveness = status.ConductorRunning;
        var state = _lastStableLiveness == ConductorLiveness.Running ? "running" : "stopped";
        var protectedParts = new List<string> { "conductor: " + state };
        if (gate is not null) protectedParts.Add(OwnerConsoleGateFormatter.StatusSegment(gate));
        protectedParts.Add($"active: {status.ActiveGoals}");
        protectedParts.Add($"decisions: {status.LiveDecisions}");
        var optionalParts = new List<string> {
            $"last event: {Age(status.LastEventAge)}", $"landed today: {status.LandedToday}", $"bell: {bellText}" };
        if (status.HiddenQuestions > 0) optionalParts.Add($"hidden: {status.HiddenQuestions}");
        if (refreshedAt is { } refreshed)
        {
            var age = now - refreshed;
            optionalParts.Add(age.TotalSeconds > 3 * refreshInterval.TotalSeconds
                ? $"stale {Math.Max(1, (int)age.TotalMinutes)}m"
                : $"refreshed {TimeZoneInfo.ConvertTime(refreshed, localZone):HH:mm:ss}");
        }
        if (status.FailedToday > 0) optionalParts.Add($"failed today: {status.FailedToday}");
        if (working.Count > 0) optionalParts.Add($"working: {string.Join(", ", working)}");
        var separator = " | ";
        string Assemble() => string.Join(separator, protectedParts.Concat(optionalParts))
            .Replace('\r', ' ').Replace('\n', ' ');
        var text = Assemble();
        // Unlaid-out labels and existing callers have no width yet.
        if (width <= 0) return text;
        // Least important suffixes yield first; preserve their existing display order.
        while (text.GetColumns() > width && optionalParts.Count > 0)
        {
            optionalParts.RemoveAt(optionalParts.Count - 1);
            text = Assemble();
        }
        if (text.GetColumns() <= width) return text;
        protectedParts[0] = state;
        if (gate is not null) protectedParts[1] = OwnerConsoleGateFormatter.StatusSegment(gate, compact: true);
        text = Assemble();
        if (text.GetColumns() <= width) return text;
        separator = "|";
        text = Assemble();
        if (width >= 80 && gate is not null && text.GetColumns() > width)
        {
            // Long participation descriptions still leave room for the other three facts.
            var available = width - (text.GetColumns() - protectedParts[1].GetColumns());
            var failing = gate.Failing || gate.ConcurrentRuns?.Any(run => run.Failing) == true;
            protectedParts[1] = failing
                ? Fit(protectedParts[1].Replace(" FAILING", "", StringComparison.Ordinal), available - 8) + " FAILING"
                : Fit(protectedParts[1], available);
            return Assemble();
        }
        // Below 80, decisions, active count, then gate yield before the conductor.
        while (text.GetColumns() > width && protectedParts.Count > 1)
        {
            protectedParts.RemoveAt(protectedParts.Count - 1);
            text = Assemble();
        }
        return Fit(text, width);
    }

    private static string Fit(string text, int width)
    {
        if (text.GetColumns() <= width) return text;
        var result = new StringBuilder();
        var columns = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            var size = rune.GetColumns();
            if (columns + size > width - 1) break;
            result.Append(rune);
            columns += size;
        }
        result.Append('…');
        return result.ToString();
    }

    private static string Age(TimeSpan? span) => span is null ? "unknown" :
        span.Value.TotalMinutes < 1 ? $"{span.Value.TotalSeconds:0}s" : span.Value.TotalHours < 1 ? $"{span.Value.TotalMinutes:0}m" : $"{span.Value.TotalHours:0}h";
}
