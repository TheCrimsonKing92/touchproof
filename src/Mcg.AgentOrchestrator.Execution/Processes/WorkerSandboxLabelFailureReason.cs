namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorkerSandboxLabelFailureReason
{
    internal static string Build(string path, WorktreeDescendantIntegrityScanResult? descendants,
        IntegrityLabelCommandResult? nativeResult)
    {
        var reason = $"Failed to apply inheritable Low integrity label to '{path}'.";
        if (descendants is not null)
            reason += $" Descendant integrity: {descendants.Outcome}; path={descendants.Path}.";
        if (nativeResult is not null)
            reason += $" Native label command: {nativeResult.Diagnostic}";
        return reason;
    }
}
