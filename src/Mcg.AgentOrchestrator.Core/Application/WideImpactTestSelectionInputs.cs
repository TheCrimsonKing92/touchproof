namespace Mcg.AgentOrchestrator.Core;

internal static class WideImpactTestSelectionInputs
{
    private const string KernelTypeName = nameof(AgentOrchestratorKernel);

    // These sources shape briefs consumed by tests throughout the kernel's callers.
    private static readonly IReadOnlyList<string> BriefBuildingSourcePaths = Array.AsReadOnly<string>(
    [
        "src/Mcg.AgentOrchestrator.Core/WorkerStandingRules.cs",
        "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefs.cs",
        "src/Mcg.AgentOrchestrator.Core/Application/AgentOrchestratorKernel.TaskBriefProjection.cs"
    ]);

    internal static IReadOnlyList<string> GetSeedTypeNames(IReadOnlyList<string> normalizedChangedPaths) =>
        normalizedChangedPaths.Any(path => BriefBuildingSourcePaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            ? [KernelTypeName]
            : [];
}
