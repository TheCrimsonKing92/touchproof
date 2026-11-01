using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ExtractedVerifierHelperTypesTests
{
    // Oracles derived from verifier bodies at pre-extraction commit 7bc9e7dd0:
    // GoalAcceptanceVerifier.MtpFailureOutput.cs, .NegativeControlDeclaredSet.cs,
    // and .OwnerResultsRooting.cs; the TRX failure lines also have a preexisting
    // literal oracle in GoalAcceptanceVerifierDotnetBuildSlotTestsRunnerInvocationAndTrx.
    [Fact]
    public void MtpFailurePreservesCommandOutputAndFailedTests()
    {
        var fixturePath = Path.Combine(
            InfrastructureTestSupport.FindRepositoryRoot(),
            "tests", "Mcg.AgentOrchestrator.Infrastructure.Tests", "TestData", "Fixtures",
            "mtp-xunit-v3-failures.trx.xml");

        var output = AcceptanceMtpFailureOutput.BuildMtpFailureOutputForTests(
            "infrastructure tests", new GoalAcceptanceVerifier.CommandResult(1, "Runner failed."),
            [fixturePath]);

        Assert.Equal(string.Join(Environment.NewLine,
            "Runner failed.",
            "[FAIL] Mcg.AgentOrchestrator.Infrastructure.Tests.RetryEvidenceTests.IncludesFailures: Assert.Contains() Failure: Sub-string not found",
            "[FAIL] Mcg.AgentOrchestrator.Infrastructure.Tests.MtpShardTests.PreservesTheoryDisplayName(value: 42): Expected shard count to be 2, but found 1"), output);
    }

    [Fact]
    public void MtpFailureWithoutTrxPreservesCrashDiagnostic()
    {
        var output = AcceptanceMtpFailureOutput.BuildMtpFailureOutputForTests(
            "infrastructure tests", new GoalAcceptanceVerifier.CommandResult(1, string.Empty), []);

        Assert.Equal(
            "[FAIL] infrastructure tests: failed — no TRX produced (shard was killed or crashed before reporter flushed)",
            output);
    }

    [Theory]
    [InlineData("tests/HelperTests.cs", true)]
    [InlineData("tests/UndeclaredTests.cs", false)]
    [InlineData("tests/helpertests.cs", false)]
    public void DeclaredRevertPathsPreserveExactMembership(string path, bool expected)
    {
        var declared = NegativeControlRevertPaths.ValidDeclaredRevertPaths(["tests/HelperTests.cs"]);

        Assert.NotNull(declared);
        Assert.Equal(["tests/HelperTests.cs"], declared);
        Assert.Equal(expected, NegativeControlRevertPaths.IsDeclaredRevertPath(path, declared));
    }

    [Fact]
    public void MissingDeclarationRefusesRevertPath()
    {
        var declared = NegativeControlRevertPaths.ValidDeclaredRevertPaths(null);

        Assert.Null(declared);
        Assert.False(NegativeControlRevertPaths.IsDeclaredRevertPath("tests/HelperTests.cs", declared));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkedWorktreeResolvesMainRepositoryRoot(bool nestedCandidate)
    {
        var root = Directory.CreateTempSubdirectory("extracted-verifier-helpers-").FullName;
        try
        {
            var repository = Path.Combine(root, "repo");
            var gitDirectory = Path.Combine(repository, ".git", "worktrees", "candidate");
            var worktree = Path.Combine(repository, ".orchestrator-worktrees", "candidate");
            Directory.CreateDirectory(gitDirectory);
            Directory.CreateDirectory(worktree);
            File.WriteAllText(Path.Combine(worktree, ".git"), $"gitdir: {gitDirectory}");
            File.WriteAllText(Path.Combine(gitDirectory, "commondir"), "../..");
            var candidate = nestedCandidate ? Path.Combine(worktree, "src", "nested") : worktree;
            Directory.CreateDirectory(candidate);

            Assert.Equal(repository, AcceptanceOwnerResultsRoot.ResolveOwnerResultsRootForCandidate(candidate));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
