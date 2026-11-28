namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class WorkerBuildScopeToolCacheIdentity
{
    internal const string EnvironmentVariable = "MCG_WORKER_BUILD_SCOPE_TOOL_CACHE";
    internal const string ClassPattern = "worker-build-scope-tool-cache";

    internal static string Root() => Resolve(
        Environment.GetEnvironmentVariable(EnvironmentVariable),
        Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        () => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.CurrentDirectory);

    // Keep in sync with Get-WorkerBuildScopeToolCacheRoot in the publisher script.
    internal static string Resolve(string? overrideValue, string? localAppData, Func<string> specialFolder, string currentDirectory)
    {
        if (!string.IsNullOrWhiteSpace(overrideValue))
            return Path.GetFullPath(Path.IsPathRooted(overrideValue) ? overrideValue : Path.Combine(currentDirectory, overrideValue));

        if (string.IsNullOrWhiteSpace(localAppData)) localAppData = specialFolder();
        if (string.IsNullOrWhiteSpace(localAppData))
            throw new InvalidOperationException("Scope tool cache requires local application data.");

        return Path.GetFullPath(Path.Combine(localAppData, "Mcg.AgentOrchestrator", "tool-cache", "worker-build-scope"));
    }
}
