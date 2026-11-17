namespace Mcg.AgentOrchestrator.Infrastructure;

public static class PreLandingConflictRemedy
{
    public static string Build(string goalPrefix, IReadOnlyList<string> orderedConflictPaths)
    {
        ArgumentNullException.ThrowIfNull(goalPrefix);
        ArgumentNullException.ThrowIfNull(orderedConflictPaths);

        return $"Conflicting paths (in order): {string.Join(", ", orderedConflictPaths)}. " +
            $"Run conduct {goalPrefix} --loop. " +
            "The conductor runs one conflict-integration round per candidate head; " +
            "only if that round fails does the operator integrate main into the goal branch.";
    }
}
