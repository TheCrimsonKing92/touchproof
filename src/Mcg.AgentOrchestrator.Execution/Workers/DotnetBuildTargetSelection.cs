namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record DotnetBuildTargetSelection(
    IReadOnlyList<string> Targets,
    IReadOnlyList<string> Projects,
    string? AbsenceProof = null,
    string? Error = null)
{
    internal IReadOnlyList<string> WithRequiredProjects(IEnumerable<string> requiredProjects) =>
        Targets.Concat(requiredProjects.Except(Projects, StringComparer.OrdinalIgnoreCase)).ToArray();
}
