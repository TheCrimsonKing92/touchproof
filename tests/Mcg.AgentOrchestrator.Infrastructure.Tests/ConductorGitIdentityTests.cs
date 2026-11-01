using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each integration case owns its temporary repository and pins identity per git child.
public sealed class ConductorGitIdentityTests
{
    [Theory]
    [InlineData("touchproof", "touchproof@localhost")]
    [InlineData("Touchproof Acceptance Gate", "acceptance-gate@localhost")]
    [InlineData("mcg-orchestrator", "mcg-orchestrator@localhost")]
    [InlineData("MCG Acceptance Gate", "acceptance-gate@localhost")]
    public void IsConductorCommitter_CurrentAndLegacyPairs_Accepts(string name, string email)
    {
        Assert.True(ConductorGitIdentity.IsConductorCommitter(name, email));
    }

    [Theory]
    [InlineData("worker", "worker@example.invalid")]
    [InlineData("touchproof", "worker@example.invalid")]
    [InlineData("Touchproof Acceptance Gate", "worker@example.invalid")]
    [InlineData("mcg-orchestrator", "touchproof@localhost")]
    [InlineData("touchproof", "mcg-orchestrator@localhost")]
    [InlineData("Touchproof Canary", "canary@localhost")]
    [InlineData("MCG Canary", "canary@localhost")]
    [InlineData("Touchproof", "touchproof@localhost")]
    [InlineData("touchproof", "TOUCHPROOF@localhost")]
    [InlineData("touchproof ", "touchproof@localhost")]
    [InlineData("touchproof", "touchproof@localhost ")]
    [InlineData("", "")]
    [InlineData(null, "touchproof@localhost")]
    [InlineData("touchproof", null)]
    [InlineData(null, null)]
    public void IsConductorCommitter_UnmatchedPairs_Rejects(string? name, string? email)
    {
        Assert.False(ConductorGitIdentity.IsConductorCommitter(name, email));
    }

    [Fact]
    public void MainIntegrationSquash_MergeCandidate_WritesTouchproofCommitter()
    {
        using var repo = new ConductorOnlyCandidateRangeTests.Repository();
        repo.Merge();
        var candidate = repo.Head;
        Assert.Equal("1", repo.Git("rev-list", "--merges", "--count", $"main..{candidate}"));
        var mergeBase = repo.Git("merge-base", "main", candidate);
        var calls = 0;

        var sha = MainIntegrationSquash.TryCreate(repo.Root, "main", mergeBase, candidate,
            GoalId.New(), args =>
            {
                calls++;
                var result = InfrastructureTestSupport.RunGitProbe(repo.Root, args);
                Assert.True(result.Succeeded, result.StandardError);
                return new GitCli.GitResult(result.ExitCode!.Value,
                    result.StandardOutput, result.StandardError);
            });

        Assert.Equal(1, calls);
        Assert.NotNull(sha);
        var committer = InfrastructureTestSupport.RunGitProbe(repo.Root,
            ["log", "-1", "--format=%cn <%ce>", sha]);
        Assert.True(committer.Succeeded, committer.StandardError);
        Assert.Equal("touchproof <touchproof@localhost>", committer.StandardOutput.Trim());
    }

    [Theory]
    [InlineData("touchproof@localhost", true)]
    [InlineData("worker@example.invalid", false)]
    public void IsConductorOnly_LegacyThenCurrentMerge_ChecksExactCurrentPair(
        string currentEmail, bool expected)
    {
        using var repo = new ConductorOnlyCandidateRangeTests.Repository();
        repo.Merge("mcg-orchestrator", "mcg-orchestrator@localhost");
        var legacyMerge = repo.Head;
        repo.Git("checkout", "main");
        repo.Write("later-upstream.txt", "later upstream advancement\n");
        ConductorOnlyCandidateRangeTests.Commit(repo.Root, "upstream", "upstream@example.invalid");
        repo.Git("checkout", "goal");
        repo.Merge("touchproof", currentEmail);

        Assert.Equal(legacyMerge, repo.Git("rev-parse", "HEAD^1"));
        Assert.Equal("2", repo.Git("rev-list", "--first-parent", "--merges", "--count",
            $"{repo.Reviewed}..HEAD"));
        Assert.Equal($"touchproof <{currentEmail}>", repo.Git("log", "-1", "--format=%cn <%ce>"));
        Assert.Equal(expected, repo.Eligible());
    }
}
