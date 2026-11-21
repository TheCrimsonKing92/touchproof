namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ShadowStartPathTrigger
{
    private static readonly IReadOnlyList<string> ProductionTriggerPaths =
    [
        "src/Mcg.AgentOrchestrator.App/Orchestration/",
        "src/Mcg.AgentOrchestrator.Core/Conductor/",
        "src/Mcg.AgentOrchestrator.Execution/Persistence/",
        "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel",
        "src/Mcg.AgentOrchestrator.Core/Application/AcceptanceCriteriaParser",
        "src/Mcg.AgentOrchestrator.App/Cli/CliArgumentParser",
        "src/Mcg.AgentOrchestrator.App/Cli/CliCommandHandlers.Goals",
        "src/Mcg.AgentOrchestrator.App/Cli/CliPersistentStateRunner",
        "src/Mcg.AgentOrchestrator.App/Program",
        "src/Mcg.AgentOrchestrator.Execution/Workers/WorkerResultParser",
        "src/Mcg.AgentOrchestrator.Execution/Workers/WorkerResultContractParser"
    ];

    internal static bool Touches(IEnumerable<string> changedPaths)
    {
        foreach (var path in changedPaths)
        {
            var normalized = path.Replace('\\', '/');
            while (normalized.StartsWith("./", StringComparison.Ordinal))
                normalized = normalized[2..];
            if (ProductionTriggerPaths.Any(prefix => Matches(normalized, prefix)))
                return true;
        }
        return false;
    }

    private static bool Matches(string path, string prefix)
    {
        const StringComparison comparison = StringComparison.OrdinalIgnoreCase;
        if (!path.StartsWith(prefix, comparison) || !path.EndsWith(".cs", comparison))
            return false;
        return prefix.EndsWith('/') || (path.Length > prefix.Length && path[prefix.Length] == '.' &&
            !path.AsSpan(prefix.Length).Contains('/'));
    }
}
