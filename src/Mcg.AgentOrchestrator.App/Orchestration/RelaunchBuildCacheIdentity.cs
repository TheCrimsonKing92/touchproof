using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Producer and retention must agree on both the cache identity and its exclusion file.
internal static class RelaunchBuildCacheIdentity
{
    internal const string LockFileName = "build.lock";

    internal static string Parent => Path.Combine(OrchestratorTempRoot.GetRoot(), "tmp", "relaunch-build");

    internal static string ProjectKey(string appProjectPath)
    {
        var projectPath = Path.GetFullPath(appProjectPath);
        if (OperatingSystem.IsWindows()) projectPath = projectPath.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(projectPath)))[..16];
    }

    internal static string RepositoryKey(string repositoryRoot) => ProjectKey(Path.Combine(
        repositoryRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"));
}
