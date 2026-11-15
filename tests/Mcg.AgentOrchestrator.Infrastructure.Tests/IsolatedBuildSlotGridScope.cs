using Mcg.AgentOrchestrator.Infrastructure;

internal sealed class IsolatedBuildSlotGridScope : IDisposable
{
    private readonly IDisposable _processSnapshotScope;

    public IsolatedBuildSlotGridScope(DotnetBuildStorageRoot storageRoot)
    {
        ArgumentNullException.ThrowIfNull(storageRoot);
        var rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(storageRoot.RootPath));
        var tempPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()));
        var tempPrefix = Path.EndsInDirectorySeparator(tempPath)
            ? tempPath
            : tempPath + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (rootPath.Equals(tempPath, comparison) || !rootPath.StartsWith(tempPrefix, comparison))
        {
            throw new ArgumentException("Build slot isolation requires a root beneath the temporary directory.", nameof(storageRoot));
        }

        _processSnapshotScope = DotnetBuildEnvironmentManager.UseProcessCommandLineSnapshot(
            () => new ProcessCommandLineSnapshot(new Dictionary<int, string>()));
    }

    public void Dispose() => _processSnapshotScope.Dispose();
}
