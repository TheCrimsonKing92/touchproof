using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record EnvironmentProbeContext(string WorkingDirectory, ConductorChildEnvironment Environment);
