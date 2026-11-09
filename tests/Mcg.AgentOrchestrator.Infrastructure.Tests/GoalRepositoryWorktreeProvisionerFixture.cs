using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

internal sealed class GoalRepositoryWorktreeProvisionerFixture : IDisposable
{
    internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
    internal string CheckoutPath => Path.Combine(Root, "checkout");
    internal string IntegrationBranch { get; }

    internal GoalRepositoryWorktreeProvisionerFixture(string integrationBranch = "main")
    {
        IntegrationBranch = integrationBranch;
        try
        {
            if (GoalRepositoryWorktreeProvisionerSeedTemplate.TryCopyTo(CheckoutPath, integrationBranch))
                return;

            Directory.CreateDirectory(CheckoutPath);
            Git("init", "-b", integrationBranch);
            Git("config", "user.name", "Worktree Provisioner Tests");
            Git("config", "user.email", "provisioner@example.com");
            Git("config", "commit.gpgsign", "false");
            Git("config", "core.autocrlf", "false");
            File.WriteAllText(Path.Combine(CheckoutPath, "example.txt"), "seed\n");
            Git("add", "example.txt");
            Git("commit", "-m", "Seed repository");
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal string WorktreePath(GoalId goal, string key) =>
        GoalRepositoryWorktreeLayout.WorktreePath(CheckoutPath, goal, key);

    internal string Branches() => Git("for-each-ref", "--format=%(refname) %(objectname)", "refs/heads");
    internal string Worktrees() => Git("worktree", "list", "--porcelain");
    internal string Git(params string[] arguments) => Run(CheckoutPath, arguments);

    internal static string Run(string directory, params string[] arguments)
    {
        if (arguments.Length == 2 && arguments[0] == "rev-parse" &&
            (arguments[1] == "HEAD" ||
             (arguments[1].StartsWith("refs/heads/", StringComparison.Ordinal) && arguments[1].Length > "refs/heads/".Length)) &&
            GitProbeRefFileFastPath.TryAnswer(directory, arguments, hasSeamOverrideOrHook: false, out var answer))
            return answer.StandardOutput.TrimEnd('\r', '\n');

        var result = GitCli.Run(directory, arguments);
        Assert.True(result.Succeeded && !result.DrainTimedOut && result.ProcessStarted, result.Error);
        return result.Output.TrimEnd('\r', '\n');
    }

    internal static string CommitChange(string path, string content = "changed\n")
    {
        File.WriteAllText(Path.Combine(path, "example.txt"), content);
        Run(path, "add", "example.txt");
        Run(path, "commit", "-m", "Change example");
        return Run(path, "rev-parse", "HEAD");
    }

    internal static Dictionary<string, string> Checkouts(
        GoalRepositoryWorktreeProvisionerFixture alpha, GoalRepositoryWorktreeProvisionerFixture beta) =>
        new(StringComparer.Ordinal) { ["beta"] = beta.CheckoutPath, ["alpha"] = alpha.CheckoutPath };

    internal void AssertAbsent(GoalId goal, string key)
    {
        var path = WorktreePath(goal, key);
        Assert.False(Path.Exists(path));
        var branch = GoalRepositoryWorktreeLayout.BranchName(goal, key);
        var result = GitCli.Run(CheckoutPath, "show-ref", "--verify", "--quiet", "refs/heads/" + branch);
        Assert.Equal(1, result.ExitCode);
        Assert.False(result.DrainTimedOut);
        Assert.DoesNotContain("branch refs/heads/" + branch + "\n", Worktrees() + "\n");
        var paths = Git("worktree", "list", "--porcelain", "-z").Split('\0')
            .Where(field => field.StartsWith("worktree ", StringComparison.Ordinal))
            .Select(field => Path.GetFullPath(field[9..]));
        Assert.DoesNotContain(paths, registeredPath => string.Equals(Path.GetFullPath(path), registeredPath,
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
    }

    public void Dispose()
    {
        try
        {
            if (!Directory.Exists(Path.Combine(CheckoutPath, ".git"))) return;
            var fields = Git("worktree", "list", "--porcelain", "-z").Split('\0');
            foreach (var field in fields.Where(field => field.StartsWith("worktree ", StringComparison.Ordinal)))
            {
                var path = Path.GetFullPath(field[9..]);
                if (path.Equals(Path.GetFullPath(CheckoutPath), StringComparison.OrdinalIgnoreCase)) continue;
                Assert.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase);
                Git("worktree", "remove", "--force", "--force", path);
            }
        }
        finally { SharedTestSupport.RemoveTempDirectory(Root); }
    }
}
