namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Published repair commits survive a later checkout synchronization failure.</summary>
internal sealed record RepositoryLandingCompensationResult(IReadOnlyList<string> Commits, string? Error = null);
