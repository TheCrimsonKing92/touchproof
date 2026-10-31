namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record EnvironmentProbeResult(EnvironmentToolRequirement Tool, bool Passed, int? ExitCode,
    string OutputTail, string Outcome);
