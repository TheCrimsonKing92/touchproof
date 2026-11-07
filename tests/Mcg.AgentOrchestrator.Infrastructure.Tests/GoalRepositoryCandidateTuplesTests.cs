using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each real-Git case owns its repositories and linked worktrees under unique temp roots.
public sealed class GoalRepositoryCandidateTuplesTests
{
    [Fact]
    public void TryCompute_RealRepositories_PreservesEachIdentityAndBranch()
    {
        using var alpha = new RepositoryFixture("main");
        using var beta = new RepositoryFixture("trunk");
        var bindings = Bindings(alpha, beta);
        var manifestCalls = new List<string>();

        var ok = GoalRepositoryCandidateTuples.TryCompute(new GoalRepositorySet(["beta", "alpha"]), bindings,
            out var tuple, out var reason, (path, changedFiles) =>
            {
                Assert.Contains("example.txt", changedFiles);
                manifestCalls.Add(path);
                return "manifest";
            });

        Assert.True(ok, reason);
        Assert.Equal(string.Empty, reason);
        Assert.NotNull(tuple);
        Assert.Equal(new[] { alpha.WorktreePath, beta.WorktreePath }, manifestCalls);
        Assert.Equal(new[] { "alpha", "beta" }, tuple.Entries.Select(entry => entry.Key));
        foreach (var entry in tuple.Entries)
        {
            var binding = bindings[entry.Key];
            Assert.True(GoalWorktrees.TryComputeCandidateIdentity(binding.WorktreePath, binding.IntegrationBranch,
                out var expected, out var failure, (_, _) => "manifest"), failure);
            Assert.Equal(expected, entry.Value);
        }
    }

    [Fact]
    public void TryCompute_MissingWorktree_ReturnsNoTupleAndKeyPrefixedReason()
    {
        using var alpha = new RepositoryFixture();
        var missing = Path.Combine(alpha.Root, "missing-worktree");
        Assert.False(Directory.Exists(missing));
        var bindings = new Dictionary<string, (string WorktreePath, string IntegrationBranch)>
        {
            ["alpha"] = (alpha.WorktreePath, alpha.IntegrationBranch),
            ["beta"] = (missing, "main")
        };

        Assert.False(GoalWorktrees.TryComputeCandidateIdentity(missing, "main", out _, out var ownReason, (_, _) => "manifest"));
        Assert.False(GoalRepositoryCandidateTuples.TryCompute(new GoalRepositorySet(["alpha", "beta"]), bindings,
            out var tuple, out var reason, (_, _) => "manifest"));
        Assert.Null(tuple);
        Assert.Equal("missing-worktree", ownReason);
        Assert.Equal("beta:" + ownReason, reason);
    }

    [Fact]
    public void TryCompute_DirtyWorktree_ReturnsNoTupleAndKeyPrefixedReason()
    {
        using var alpha = new RepositoryFixture();
        using var beta = new RepositoryFixture();
        File.WriteAllText(Path.Combine(beta.WorktreePath, "untracked.txt"), "uncommitted\n");
        Assert.True(GitCli.IsWorktreeDirty(beta.WorktreePath));
        Assert.False(GoalWorktrees.TryComputeCandidateIdentity(beta.WorktreePath, "main",
            out _, out var ownReason, (_, _) => "manifest"));

        Assert.False(GoalRepositoryCandidateTuples.TryCompute(new GoalRepositorySet(["alpha", "beta"]), Bindings(alpha, beta),
            out var tuple, out var reason, (_, _) => "manifest"));
        Assert.Null(tuple);
        Assert.Equal("dirty-worktree", ownReason);
        Assert.Equal("beta:" + ownReason, reason);
    }

    [Fact]
    public void TryCompute_ValidBindings_ComputesOncePerKeyInOrdinalOrder()
    {
        var bindings = StubBindings();
        var calls = new List<(string Path, string Branch)>();
        Assert.True(GoalRepositoryCandidateTuples.TryCompute(new GoalRepositorySet(["beta", "alpha"]), bindings,
            (path, branch) =>
            {
                calls.Add((path, branch));
                return (true, new CandidateIdentity(path, branch, "manifest"), string.Empty);
            }, out var tuple, out var reason));

        Assert.Equal(new[] { ("alpha-path", "main"), ("beta-path", "trunk") }, calls);
        Assert.Equal(string.Empty, reason);
        Assert.NotNull(tuple);
        Assert.Equal(new[] { "alpha", "beta" }, tuple.Entries.Select(entry => entry.Key));
        Assert.Equal(new CandidateIdentity("alpha-path", "main", "manifest"), tuple.Entries[0].Value);
        Assert.Equal(new CandidateIdentity("beta-path", "trunk", "manifest"), tuple.Entries[1].Value);
    }

    [Fact]
    public void TryCompute_FirstRepositoryFails_StopsAndPreservesUnderlyingReason()
    {
        var calls = new List<string>();
        Assert.False(GoalRepositoryCandidateTuples.TryCompute(new GoalRepositorySet(["beta", "alpha"]), StubBindings(),
            (path, branch) =>
            {
                calls.Add(path);
                return (false, null, "candidate-computation-failed:IOException:original detail");
            }, out var tuple, out var reason));

        Assert.Equal(new[] { "alpha-path" }, calls);
        Assert.Null(tuple);
        Assert.Equal("alpha:candidate-computation-failed:IOException:original detail", reason);
    }

    [Theory]
    [InlineData("missing", "beta:missing-binding")]
    [InlineData("extra", "gamma:unexpected-binding")]
    [InlineData("case", "alpha:missing-binding")]
    public void TryCompute_InvalidCoverage_FailsBeforeAnyComputation(string scenario, string expected)
    {
        var bindings = new Dictionary<string, (string WorktreePath, string IntegrationBranch)>(StubBindings(), StringComparer.OrdinalIgnoreCase);
        if (scenario == "missing") bindings.Remove("beta");
        if (scenario == "extra") bindings.Add("gamma", ("gamma-path", "main"));
        if (scenario == "case")
        {
            bindings.Remove("alpha");
            bindings.Add("ALPHA", ("alpha-path", "main"));
        }
        var calls = 0;
        Assert.False(GoalRepositoryCandidateTuples.TryCompute(new GoalRepositorySet(["alpha", "beta"]), bindings,
            (_, _) =>
            {
                calls++;
                return (true, new CandidateIdentity("patch", "base", "manifest"), string.Empty);
            }, out var tuple, out var reason));

        Assert.Null(tuple);
        Assert.Equal(expected, reason);
        Assert.Equal(0, calls);
    }

    [Fact]
    public void TryCompute_NullTopLevelArgument_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => GoalRepositoryCandidateTuples.TryCompute(null!, StubBindings(), out _, out _));
        Assert.Throws<ArgumentNullException>(() => GoalRepositoryCandidateTuples.TryCompute(new GoalRepositorySet(["alpha"]), null!, out _, out _));
    }

    [Fact]
    public void TryCompute_SuccessWithoutIdentity_ThrowsLoudly()
    {
        Assert.Throws<InvalidOperationException>(() => GoalRepositoryCandidateTuples.TryCompute(
            new GoalRepositorySet(["alpha", "beta"]), StubBindings(),
            (_, _) => (true, null, string.Empty), out _, out _));
    }

    internal static Dictionary<string, (string WorktreePath, string IntegrationBranch)> Bindings(
        RepositoryFixture alpha, RepositoryFixture beta) => new(StringComparer.Ordinal)
        {
            ["beta"] = (beta.WorktreePath, beta.IntegrationBranch),
            ["alpha"] = (alpha.WorktreePath, alpha.IntegrationBranch)
        };

    private static Dictionary<string, (string WorktreePath, string IntegrationBranch)> StubBindings() => new(StringComparer.Ordinal)
    {
        ["beta"] = ("beta-path", "trunk"),
        ["alpha"] = ("alpha-path", "main")
    };

    internal sealed class RepositoryFixture : IDisposable
    {
        internal string Root { get; } = SharedTestSupport.CreateTempDirectory();
        internal string RepositoryPath => Path.Combine(Root, "repository");
        internal string WorktreePath => Path.Combine(Root, "worktree");
        internal string IntegrationBranch { get; }
        internal string Head => Git(WorktreePath, "rev-parse", "HEAD").Output.Trim();

        internal RepositoryFixture(string integrationBranch = "main")
        {
            IntegrationBranch = integrationBranch;
            try
            {
                Directory.CreateDirectory(RepositoryPath);
                Git(RepositoryPath, "init", "-b", integrationBranch);
                Git(RepositoryPath, "config", "user.email", "repository-candidates@example.com");
                Git(RepositoryPath, "config", "user.name", "Repository Candidates");
                Git(RepositoryPath, "config", "commit.gpgsign", "false");
                Git(RepositoryPath, "config", "core.autocrlf", "false");
                File.WriteAllText(Path.Combine(RepositoryPath, "example.txt"), "value = 1\n");
                Git(RepositoryPath, "add", "example.txt");
                Git(RepositoryPath, "commit", "-m", "Seed");
                Git(RepositoryPath, "worktree", "add", WorktreePath, "-b", "goal/candidate", integrationBranch);
                CommitChange("value = 2\n");
                Assert.False(GitCli.IsWorktreeDirty(WorktreePath));
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal void CommitChange(string content)
        {
            File.WriteAllText(Path.Combine(WorktreePath, "example.txt"), content);
            Git(WorktreePath, "add", "example.txt");
            Git(WorktreePath, "commit", "-m", "Change value");
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path.Combine(WorktreePath, ".git")))
                    Git(RepositoryPath, "worktree", "remove", "--force", WorktreePath);
            }
            finally
            {
                SharedTestSupport.RemoveTempDirectory(Root);
            }
        }

        private static GitCli.GitResult Git(string directory, params string[] arguments)
        {
            var result = GitCli.Run(directory, 10_000, arguments);
            Assert.True(result.Succeeded && !result.DrainTimedOut, result.Error);
            return result;
        }
    }
}
