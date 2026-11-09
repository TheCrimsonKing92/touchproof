using System.Reflection;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture-owned repositories and AsyncLocal Git runner overrides.
public sealed class LandingExecutorRefFileResolutionTests
{
    private static readonly Func<string, string, string?> TryResolveBranch = Helper<string?>("TryResolveBranch");
    private static readonly Func<string, string, string> ResolveRef = Helper<string>("ResolveRef");
    private static readonly Func<string, string, bool> BranchExists = Helper<bool>("BranchExists");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ProvenReferencesAndAbsence_MatchOriginalGitWithoutRunnerCalls(bool linked)
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        fixture.Git("branch", "packed");
        fixture.Git("pack-refs", "--all");
        fixture.Git("branch", "loose");
        var repo = Workspace(fixture, linked);

        foreach (var branch in new[] { "loose", "packed", "missing", "HEAD", "refs/heads/loose" })
        {
            var expected = GitCli.Run(repo, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");
            Assert.True(GitRefFileResolver.TryResolve(repo, $"refs/heads/{branch}", out _) ||
                GitRevisionReader.TryConfirmBranchAbsent(repo, branch));
            using var runner = LandingExecutor.UseGitRunner((_, _) =>
                throw new InvalidOperationException("A proved branch must not start git."));
            Assert.Equal(expected.Succeeded ? expected.Output.Trim() : null, TryResolveBranch(repo, branch));
            Assert.Equal(expected.Succeeded, BranchExists(repo, branch));
        }

        foreach (var reference in new[] { "HEAD", "loose", "refs/heads/loose", "refs/heads/packed" })
        {
            var expected = GitCli.Run(repo, "rev-parse", reference);
            Assert.True(expected.Succeeded, expected.Error);
            Assert.True(GitRefFileResolver.TryResolve(repo, reference, out _));
            using var runner = LandingExecutor.UseGitRunner((_, _) =>
                throw new InvalidOperationException("A proved reference must not start git."));
            Assert.Equal(expected.Output.Trim(), ResolveRef(repo, reference));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeclinedReferences_UseOriginalArgumentsAndException(bool linked)
    {
        using var fixture = new GitRefFileResolverParityTests.Fixture();
        fixture.Git("branch", "locked");
        File.WriteAllText(Path.Combine(fixture.Repo, ".git", "refs", "heads", "locked.lock"), "");
        fixture.Git("update-ref", "refs/remotes/refs/heads/remote-only", fixture.Oracle("HEAD"));
        fixture.Git("tag", "-a", "fixture-main", "-m", "ambiguous shorthand");
        var repo = Workspace(fixture, linked);
        var calls = new List<string[]>();
        using var runner = LandingExecutor.UseGitRunner((directory, arguments) =>
        {
            Assert.Equal(repo, directory);
            calls.Add(arguments);
            return GitCli.Run(directory, arguments);
        });

        foreach (var branch in new[] { "locked", "remote-only" })
        {
            Assert.False(GitRefFileResolver.TryResolve(repo, $"refs/heads/{branch}", out _));
            Assert.False(GitRevisionReader.TryConfirmBranchAbsent(repo, branch));
            string[] arguments = ["rev-parse", "--verify", "--quiet", $"refs/heads/{branch}"];
            var expected = GitCli.Run(repo, arguments);
            Assert.True(expected.Succeeded, expected.Error);
            calls.Clear();
            Assert.Equal(expected.Output.Trim(), TryResolveBranch(repo, branch));
            Assert.Equal(arguments, Assert.Single(calls));
            calls.Clear();
            Assert.True(BranchExists(repo, branch));
            Assert.Equal(arguments, Assert.Single(calls));
        }

        foreach (var reference in new[] { "refs/heads/locked", "fixture-main", "unknown-reference" })
        {
            Assert.False(GitRefFileResolver.TryResolve(repo, reference, out _));
            var expected = GitCli.Run(repo, "rev-parse", reference);
            calls.Clear();
            if (expected.Succeeded)
                Assert.Equal(expected.Output.Trim(), ResolveRef(repo, reference));
            else
                Assert.Equal($"Failed to resolve '{reference}' before landing: {expected.Error}",
                    Assert.Throws<InvalidOperationException>(() => ResolveRef(repo, reference)).Message);
            Assert.Equal(new[] { "rev-parse", reference }, Assert.Single(calls));
        }
    }

    private static string Workspace(GitRefFileResolverParityTests.Fixture fixture, bool linked)
    {
        if (!linked) return fixture.Repo;
        var path = Path.Combine(fixture.Root, "linked");
        fixture.Git("worktree", "add", "-b", "linked", path);
        Assert.True(File.Exists(Path.Combine(path, ".git")));
        return path;
    }

    private static Func<string, string, T> Helper<T>(string name) =>
        typeof(LandingExecutor).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!
            .CreateDelegate<Func<string, string, T>>();
}
