using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Captures only the goal facts available to this pass. A kernel can be a working set;
// absence from it never proves that a goal-id-shaped directory is abandoned.
internal sealed class OrphanWorktreeSweepEligibility
{
    internal static readonly TimeSpan GraceWindow = TimeSpan.FromMinutes(10);
    private readonly (GoalId Id, bool IsTerminal)[] goals;
    private readonly OrphanWorktreeStoredGoalLookup storedGoals;

    internal OrphanWorktreeSweepEligibility(AgentOrchestratorKernel? kernel,
        OrphanWorktreeStoredGoalLookup? storedGoals = null)
    {
        this.storedGoals = storedGoals ?? new(null);
        try { goals = kernel?.Goals.Select(goal => (goal.Id, goal.IsTerminal)).ToArray() ?? []; }
        catch (InvalidOperationException) { goals = []; }
    }

    internal GoalId? ResolveGoal(string directory)
    {
        var name = System.IO.Path.GetFileName(directory);
        var matches = goals.Where(goal => goal.Id.Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 0 ? storedGoals.ResolveObservedGoal(directory)
            : matches.FirstOrDefault(goal => !goal.IsTerminal, matches[0]).Id;
    }

    internal string Classify(string directory, IReadOnlySet<string> registered, DateTimeOffset referenceTime,
        IReadOnlyList<GoalWorktreeRegistration>? records = null,
        Func<string, IReadOnlyList<WorktreeLockHolder>>? findHolders = null)
    {
        if (registered.Contains(GoalWorktreeLayout.NormalizePath(directory)))
            return PrefixedWorktreeReclaim.Classify(directory, records, referenceTime, findHolders);
        var name = System.IO.Path.GetFileName(directory);
        var matches = goals.Where(goal => goal.Id.Value.StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Any(goal => !goal.IsTerminal)) return "non-terminal-goal";
        if (matches.Length == 0 && name.Length == 8 && name.All(Uri.IsHexDigit))
            return storedGoals.Classify(directory, referenceTime);

        return WorktreeTreeFreshness.Classify(directory, referenceTime, GraceWindow);
    }
}
