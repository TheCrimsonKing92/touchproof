using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerConsoleEventRoute
{
    private readonly Dictionary<string, (string State, string Result, string Reason)> _goals = new(StringComparer.OrdinalIgnoreCase);

    internal static bool IsPaneOnly(OwnerConductEvent item) =>
        item.EventKind.StartsWith("tick", StringComparison.OrdinalIgnoreCase) ||
        OwnerEventFields.Head(item) == "ACCEPTANCE" && UniqueField(item, "result") == "running";

    // Missing discriminating fields are never positive evidence of an equivalent event.
    internal bool IsRepeatedGoal(OwnerConductEvent item)
    {
        if (OwnerEventFields.Head(item) != "GOAL" ||
            (item.GoalId ?? OwnerEventFields.Field(item, "goal")) is not { } id) return false;
        if (UniqueField(item, "state") is not { } state ||
            UniqueField(item, "result") is not { } result ||
            UniqueField(item, "reason") is not { } reason)
        {
            _goals.Remove(id);
            return false;
        }
        var key = (state, result, reason);
        var repeated = _goals.TryGetValue(id, out var previous) && previous == key;
        if (!_goals.ContainsKey(id) && _goals.Count >= 1024) _goals.Remove(_goals.Keys.First());
        _goals[id] = key;
        return repeated;
    }

    private static string? UniqueField(OwnerConductEvent item, string name) =>
        Regex.Matches(item.Detail, @"(?:^|\s)" + Regex.Escape(name) + "=").Count == 1
            ? OwnerEventFields.Field(item, name) : null;
}
