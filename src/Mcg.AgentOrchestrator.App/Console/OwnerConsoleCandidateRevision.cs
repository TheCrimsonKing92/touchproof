using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Read the current ref on every observation; uncertainty retains the existing Git fallback.
internal static class OwnerConsoleCandidateRevision
{
    internal static string? Read(string directory, GoalId goal,
        Func<string, string, string?>? fallback = null)
    {
        var reference = $"refs/heads/{GoalWorktrees.BranchName(goal)}";
        if (GitRefFileResolver.TryResolve(directory, reference, out var revision)) return revision;
        if (fallback is not null) return fallback(directory, reference);
        var result = GitCli.Run(directory, "rev-parse", "--verify", reference);
        return result.Succeeded ? result.Output.Trim() : null;
    }
}
