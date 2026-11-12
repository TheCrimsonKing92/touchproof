using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One pass owns this cache. Unavailable metadata must never authorize deletion.
internal sealed class OrphanWorktreeStoredGoalLookup(Func<IReadOnlyList<GoalSummary>>? readMetadata)
{
    private IReadOnlyList<GoalSummary> metadata = [];
    private bool attempted;

    internal string Classify(string directory, DateTimeOffset referenceTime)
    {
        if (!attempted)
        {
            attempted = true;
            try { metadata = readMetadata?.Invoke() ?? []; }
            catch { metadata = []; }
        }

        var matches = Matches(directory);
        if (matches.Length == 0) return "goal-state-unavailable";
        if (matches.Any(goal => !IsTerminal(goal))) return "non-terminal-goal";
        return WorktreeTreeFreshness.Classify(directory, referenceTime, OrphanWorktreeSweepEligibility.GraceWindow);
    }

    // Reporting must not initiate a read for registered folders or failed listing/lock passes.
    internal GoalId? ResolveObservedGoal(string directory)
    {
        var matches = Matches(directory);
        return matches.Length == 0 ? null : new GoalId(matches.FirstOrDefault(goal => !IsTerminal(goal), matches[0]).Id);
    }

    private GoalSummary[] Matches(string directory)
    {
        var name = Path.GetFileName(directory);
        return name.Length == 8 && name.All(Uri.IsHexDigit)
            ? metadata.Where(goal => goal.Id.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];
    }

    private static bool IsTerminal(GoalSummary goal) =>
        Enum.TryParse<GoalStatus>(goal.Status, out var status) &&
        status is GoalStatus.Completed or GoalStatus.Failed or GoalStatus.Cancelled or GoalStatus.Superseded;
}
