using System.Text;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static class GoalRepositoryWorktreeLayout
{
    public const string RepositoriesDirectoryName = "repositories";

    public static string WorktreePath(string executionDirectory, GoalId goalId, string repositoryKey) =>
        Path.Combine(GoalWorktreeLayout.WorktreePath(executionDirectory, goalId),
            RepositoriesDirectoryName, EncodeKey(repositoryKey));

    public static string BranchName(GoalId goalId, string repositoryKey) =>
        GoalWorktreeLayout.BranchName(goalId) + "--repo-" + EncodeKey(repositoryKey);

    private static string EncodeKey(string repositoryKey)
    {
        ArgumentNullException.ThrowIfNull(repositoryKey);
        if (!GoalRepositorySet.IsCanonicalKey(repositoryKey))
            throw new ArgumentException("Repository key must already match [a-z0-9][a-z0-9._-]{0,63}.", nameof(repositoryKey));

        // Hex is injective and avoids Windows device names, trailing dots, and Git ref syntax.
        return "r-" + Convert.ToHexStringLower(Encoding.ASCII.GetBytes(repositoryKey));
    }
}
