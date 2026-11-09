using System.Runtime.CompilerServices;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class WorktreeCleanTreeFastPathRegistration
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        WorktreeCleanTreeFastPath.CommonDirectoryLocator = static (root, environment) =>
            GitRevisionReader.TryLocateRefStore(root, environment, out _, out var common) ? common : null;
        WorktreeCleanTreeFastPath.HeadTreeResolver = static (root, environment) =>
            GitRefFileResolver.TryResolve(root, "HEAD^{tree}", out var tree, environment) ? tree : null;
    }
}
