using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

public sealed class GoalRepositoryWorktreeLayoutTests
{
    private static readonly GoalId Goal = new("ae318ccfece44351af1a19b1a85c51b5");

    [Fact]
    public void Layout_CanonicalKeys_ProducesDistinctNestedPathsAndBranches()
    {
        var executionDirectory = Path.Combine(Path.GetTempPath(), "repository-layout");
        var goalPath = GoalWorktreeLayout.WorktreePath(executionDirectory, Goal);
        var keys = new[] { "alpha", "alpha.", "alpha_", "alpha-", "con", "a.b", "a_b", new string('a', 64) };
        var paths = keys.Select(key => GoalRepositoryWorktreeLayout.WorktreePath(executionDirectory, Goal, key)).ToArray();
        var branches = keys.Select(key => GoalRepositoryWorktreeLayout.BranchName(Goal, key)).ToArray();

        Assert.Equal(keys.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(keys.Length, branches.Distinct(StringComparer.Ordinal).Count());
        foreach (var path in paths)
        {
            Assert.NotEqual(goalPath, path);
            Assert.StartsWith(goalPath + Path.DirectorySeparatorChar, Path.GetFullPath(path), StringComparison.Ordinal);
            Assert.Equal(Path.Combine(goalPath, "repositories"), Path.GetDirectoryName(path));
            Assert.Matches("^r-[0-9a-f]+$", Path.GetFileName(path));
        }
        foreach (var branch in branches)
        {
            Assert.NotEqual(GoalWorktreeLayout.BranchName(Goal), branch);
            Assert.StartsWith(GoalWorktreeLayout.BranchName(Goal) + "--repo-r-", branch, StringComparison.Ordinal);
        }

        Assert.Equal(Path.Combine(goalPath, "repositories", "r-616c706861"), paths[0]);
        Assert.Equal("goal/ae318ccf--repo-r-616c706861", branches[0]);
        Assert.Equal(paths[0], GoalRepositoryWorktreeLayout.WorktreePath(executionDirectory, Goal, "alpha"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Alpha")]
    [InlineData(" alpha")]
    [InlineData("a b")]
    [InlineData("../alpha")]
    [InlineData("\u212A")]
    public void Layout_NoncanonicalKey_Throws(string key)
    {
        AssertInvalidKey(key);
    }

    [Fact]
    public void Layout_OverlengthKey_Throws() => AssertInvalidKey(new string('a', 65));

    [Fact]
    public void Layout_NullKey_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GoalRepositoryWorktreeLayout.WorktreePath(Path.GetTempPath(), Goal, null!));
        Assert.Throws<ArgumentNullException>(() => GoalRepositoryWorktreeLayout.BranchName(Goal, null!));
    }

    private static void AssertInvalidKey(string key)
    {
        Assert.Throws<ArgumentException>(() => GoalRepositoryWorktreeLayout.WorktreePath(Path.GetTempPath(), Goal, key));
        Assert.Throws<ArgumentException>(() => GoalRepositoryWorktreeLayout.BranchName(Goal, key));
    }
}
