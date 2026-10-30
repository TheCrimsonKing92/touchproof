namespace Mcg.AgentOrchestrator.Infrastructure;

public interface IEnvironmentProbeRunner
{
    Task<EnvironmentProbeResult> RunAsync(EnvironmentToolRequirement tool, ConductorChildEnvironment environment,
        string workingDirectory, CancellationToken cancellationToken = default);
}
