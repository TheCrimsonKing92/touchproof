using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: lexical cases have no I/O; replay cases own all filesystem state.
public sealed class DeclaredTestIdentityPathBoundaryTests
{
    [Theory]
    [InlineData("-other/tests/X.Tests.ps1")]
    [InlineData("/tests/../../elsewhere/X.Tests.ps1")]
    [InlineData("")]
    public void OutsideOrRootIdentity_IsUnchanged(string relativeSuffix)
    {
        var root = Path.Combine(Path.GetTempPath(), "declared-worktree");
        var identity = root + relativeSuffix + "::case";

        Assert.Equal(identity, DeclaredTestIdentityPaths.Relativize(identity, root));
    }

    [Fact]
    public void DifferentVolumeIdentity_IsUnchanged()
    {
        var root = Path.Combine(Path.GetTempPath(), "declared-worktree");
        var identity = (root.StartsWith("D:", StringComparison.OrdinalIgnoreCase) ? "E:" : "D:") +
            "\\elsewhere\\tests\\X.Tests.ps1::case";

        Assert.Equal(identity, DeclaredTestIdentityPaths.Relativize(identity, root));
    }

    [Fact]
    public void WindowsComparison_IgnoresCaseAndSlashesAndPreservesTestName()
    {
        var root = Path.Combine(Path.GetTempPath(), "Declared-Worktree");
        var identity = root.ToUpperInvariant().Replace('\\', '/') +
            "/tests/./unused/../Get-McgTrxFailure.Tests.ps1::uses C:\\temp\\x::suffix";

        Assert.Equal("tests/Get-McgTrxFailure.Tests.ps1::uses C:\\temp\\x::suffix",
            DeclaredTestIdentityPaths.Relativize(identity, root.Replace('/', '\\'), ignoreCase: true));
        Assert.Equal(identity, DeclaredTestIdentityPaths.Relativize(identity, root, ignoreCase: false));
    }

    [Theory]
    [InlineData("conduct-tail::version::case")]
    [InlineData("Some.Namespace.Test")]
    public void IdentityWithoutAbsolutePath_IsUnchanged(string identity)
    {
        Assert.Equal(identity, DeclaredTestIdentityPaths.Relativize(identity, Path.GetTempPath()));
    }

    [Theory]
    [InlineData("TESTS\\get-mcgtrxfailure.tests.ps1", true)]
    [InlineData("./tests/unused/../Get-McgTrxFailure.Tests.ps1", true)]
    [InlineData("tests/Other.Tests.ps1", false)]
    [InlineData("Get-McgTrxFailure.Tests.ps1", false)]
    [InlineData("../tests/Get-McgTrxFailure.Tests.ps1", false)]
    public void FileSelector_RequiresCompleteNormalizedPath(string selector, bool expected)
    {
        var row = new DeclaredTestReportRows.Row(
            DeclaredTestWorktreeIdentityReplayTests.TestFile + "::case", "case", [], "Passed", "report");

        Assert.Equal(expected, DeclaredTestFileSelector.Matches(selector, row, ignoreCase: true));
    }

    [Fact]
    public void OtherFileSelector_ReportsNotFoundWithRelativeClosestIdentities()
    {
        using var fixture = new DeclaredTestReplayFixture();
        DeclaredTestWorktreeIdentityReplayTests.WriteReport(fixture,
            GoalWorktreeLayout.WorktreePath(fixture.Gate.Root, fixture.Gate.Goal.Id));

        var result = fixture.Evaluate("tests/Other.Tests.ps1");

        Assert.Equal(GateTestObligationEvaluator.Outcome.SelectorNotFound, result.Outcome);
        Assert.Equal(new[] { "tests/Other.Tests.ps1" }, result.Selectors);
        Assert.NotNull(result.ClosestIdentities);
        Assert.Equal(2, result.ClosestIdentities.Count);
        Assert.All(result.ClosestIdentities, identity =>
            Assert.StartsWith(DeclaredTestWorktreeIdentityReplayTests.TestFile + "::", identity));
    }
}
