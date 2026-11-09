using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class MultiRepositoryBranchTipReader
{
    internal static string Read(string root, string branch, Func<string> git)
        => Read(root, branch, git, Environment.GetEnvironmentVariable);

    internal static string Read(string root, string branch, Func<string> git,
        Func<string, string?> environment)
        => GitRefFileResolver.TryResolve(root, "refs/heads/" + branch, out var id, environment)
            ? id : git();
}
