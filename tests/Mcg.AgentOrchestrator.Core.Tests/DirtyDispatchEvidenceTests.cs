using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: task records and fixed timestamps only; no shared state or I/O.
public sealed class DirtyDispatchEvidenceTests
{
    private const string Guard =
        "Developer/Tester dispatch exited 0 but left the worktree dirty";
    private const string DirtyStderr =
        Guard + ". status_short=M src/Alpha.cs | M tests/AlphaTests.cs.";
    private const string TestPassStdout =
        "Test run successful. Passed: 5, Failed: 0, Skipped: 0.";

    // Baseline fdf58b2c1: DispatchFailureClassifier.cs:347-366,1161-1207.
    // These exact values follow the pre-move recovery and extraction bodies.
    [Theory]
    [InlineData(AgentRole.Developer)]
    [InlineData(AgentRole.Tester)]
    public void Recovery_DirtyGuard_PreservesFilesAndVerification(AgentRole role)
    {
        var verification = Verification(1, TestPassStdout, DirtyStderr);
        var task = TaskWithVerification(role, verification);

        Assert.True(DirtyDispatchEvidence.IsDirtyDispatchGuardFailure(verification));
        Assert.True(DirtyDispatchEvidence.TryBuildDirtyDispatchRecovery(task, out var recovery));
        Assert.Equal("dirty-useful", recovery.Label);
        Assert.Equal(new[] { "M src/Alpha.cs", "M tests/AlphaTests.cs" }, recovery.ChangedFiles);
        Assert.Equal(new[] { TestPassStdout }, recovery.VerificationEvidence);
        Assert.Equal("C:\\repo", recovery.WorkingDirectory);
        Assert.True(recovery.HasUsefulVerification);
    }

    [Fact]
    public void Recovery_OrdinaryFailure_ReturnsNone()
    {
        var verification = Verification(1, TestPassStdout, "ordinary dispatch failure");
        var task = TaskWithVerification(AgentRole.Developer, verification);

        Assert.False(DirtyDispatchEvidence.IsDirtyDispatchGuardFailure(verification));
        Assert.False(DirtyDispatchEvidence.TryBuildDirtyDispatchRecovery(task, out var recovery));
        Assert.Equal(DirtyDispatchRecovery.None, recovery);
    }

    [Theory]
    [InlineData(AgentRole.Planner, 1)]
    [InlineData(AgentRole.Reviewer, 1)]
    [InlineData(AgentRole.Developer, 0)]
    [InlineData(AgentRole.Tester, 0)]
    public void Recovery_IneligibleRoleOrSuccess_ReturnsNone(AgentRole role, int exitCode)
    {
        var verification = Verification(exitCode, TestPassStdout, DirtyStderr);
        var task = TaskWithVerification(role, verification);

        Assert.True(DirtyDispatchEvidence.IsDirtyDispatchGuardFailure(verification));
        Assert.False(DirtyDispatchEvidence.TryBuildDirtyDispatchRecovery(task, out var recovery));
        Assert.Equal(DirtyDispatchRecovery.None, recovery);
    }

    [Fact]
    public void Recovery_MissingVerification_ReturnsNone()
    {
        var task = new TaskSpec(TaskId.New(), "Unverified dispatch", AgentRole.Developer);

        Assert.False(DirtyDispatchEvidence.TryBuildDirtyDispatchRecovery(task, out var recovery));
        Assert.Equal(DirtyDispatchRecovery.None, recovery);
    }

    [Fact]
    public void Recovery_MissingStatusAndEvidence_ReturnsUnverifiedAndUnavailable()
    {
        var verification = Verification(1, "Done. Files written.", Guard);
        var task = TaskWithVerification(AgentRole.Developer, verification);

        Assert.True(DirtyDispatchEvidence.TryBuildDirtyDispatchRecovery(task, out var recovery));
        Assert.Equal("dirty-unverified", recovery.Label);
        Assert.Equal(new[] { "unavailable" }, recovery.ChangedFiles);
        Assert.Empty(recovery.VerificationEvidence);
        Assert.Equal("C:\\repo", recovery.WorkingDirectory);
        Assert.False(recovery.HasUsefulVerification);
    }

    [Fact]
    public void Guard_LowercaseStderr_MatchesCaseInsensitively()
    {
        var verification = Verification(1, string.Empty, Guard.ToLowerInvariant());

        Assert.True(DirtyDispatchEvidence.IsDirtyDispatchGuardFailure(verification));
    }

    private static TaskVerificationRecord Verification(int exitCode, string stdout, string stderr) =>
        new("codex exec", "C:\\repo", exitCode, stdout, stderr,
            new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.Zero));

    private static TaskSpec TaskWithVerification(AgentRole role, TaskVerificationRecord verification)
    {
        var task = new TaskSpec(TaskId.New(), "Dirty dispatch evidence", role);
        task.RestoreVerificationHistory(verification);
        task.ReinstateVerification(verification);
        return task;
    }
}
