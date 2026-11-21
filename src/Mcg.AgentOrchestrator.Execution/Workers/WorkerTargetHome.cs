namespace Mcg.AgentOrchestrator.Infrastructure;

// The target repository's identity, independent of where install assets are resolved.
public sealed class WorkerTargetHome(bool isOrchestratorHome = true, Func<string, string>? acceptanceManifestPath = null,
    string? projectHomeDirectory = null)
{
    public bool IsOrchestratorHome { get; } = isOrchestratorHome;
    public Func<string, string>? AcceptanceManifestPath { get; } = acceptanceManifestPath;
    public string? ProjectHomeDirectory { get; } = projectHomeDirectory;
    internal bool UsesSelectedBuildTargets { get; init; }
    public static WorkerTargetHome Home { get; } = new();
    public static WorkerTargetHome NotHome { get; } = new(false);
}
