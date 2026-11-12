namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record WorktreeFixtureScratchCleanupResult(
    bool FoundRoots, bool TimedOut, int RemovedEntries, int RetainedEntries);
