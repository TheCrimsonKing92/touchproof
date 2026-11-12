namespace Mcg.AgentOrchestrator.Infrastructure;

// Only the last reason matters: A -> B -> A must report all three transitions.
internal sealed class OrphanWorktreeSweepOutcomeLedger
{
    internal static OrphanWorktreeSweepOutcomeLedger Process { get; } = new();
    private readonly Dictionary<string, string> reasons = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object sync = new();

    internal bool Observe(OrphanWorktreeSweepOutcome outcome)
    {
        var path = GoalWorktreeLayout.NormalizePath(outcome.Path);
        lock (sync)
        {
            if (outcome.Reason == "removed")
            {
                reasons.Remove(path);
                return true;
            }
            var changed = !reasons.TryGetValue(path, out var last) || last != outcome.Reason;
            reasons[path] = outcome.Reason;
            return changed;
        }
    }
}
