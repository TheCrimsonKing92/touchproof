using System.Globalization;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleRefreshTriggerCounts
{
    private readonly object _sync = new();
    private readonly Dictionary<OwnerConsoleRefreshTrigger, long> _counts = new();

    internal void Record(OwnerConsoleRefreshTrigger triggers)
    {
        lock (_sync)
            foreach (var trigger in Enum.GetValues<OwnerConsoleRefreshTrigger>())
                if (triggers.HasFlag(trigger)) _counts[trigger] = _counts.GetValueOrDefault(trigger) + 1;
    }

    internal string Detail()
    {
        lock (_sync)
            return string.Join(" ", Enum.GetValues<OwnerConsoleRefreshTrigger>().Select(trigger =>
                string.Create(CultureInfo.InvariantCulture, $"trigger-{Name(trigger)}={_counts.GetValueOrDefault(trigger)}")));
    }

    internal static OwnerConsoleRefreshTrigger Identify(IReadOnlyList<OwnerConductEvent>? items,
        bool skipIfBusy, OwnerConsoleRefreshScope? scope)
    {
        if (skipIfBusy) return OwnerConsoleRefreshTrigger.Periodic;
        if (items is null && scope is null) return OwnerConsoleRefreshTrigger.OwnerCommand;
        var triggers = items is { Count: > 0 } ? OwnerConsoleRefreshTrigger.ConductorEvent : 0;
        if (scope is not null && !ReferenceEquals(scope, OwnerConsoleRefreshScope.None))
            triggers |= OwnerConsoleRefreshTrigger.ChangeStream;
        return triggers;
    }

    private static string Name(OwnerConsoleRefreshTrigger trigger) => trigger switch
    {
        OwnerConsoleRefreshTrigger.ConductorEvent => "conductor-event",
        OwnerConsoleRefreshTrigger.ChangeStream => "change-stream",
        OwnerConsoleRefreshTrigger.OwnerCommand => "owner-command",
        _ => trigger.ToString().ToLowerInvariant()
    };
}
