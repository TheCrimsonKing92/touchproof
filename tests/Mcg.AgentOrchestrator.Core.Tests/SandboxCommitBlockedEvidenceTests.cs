using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: in-memory inputs, fixed timestamp, no shared state or external resources.
public sealed class SandboxCommitBlockedEvidenceTests
{
    private const string UsefulOutput = "WORKER_RESULT:\nfiles: src/Foo.cs\nEND_WORKER_RESULT";
    private const string CommitBlocked = "fatal: Unable to create '.git/index.lock': Permission denied";
    private static readonly DateTimeOffset CompletedAt = new(2026, 10, 31, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Verification_DeveloperWithCommitBlock_ReturnsTrue()
    {
        var task = new TaskSpec(TaskId.New(), "Developer task", AgentRole.Developer);
        var verification = Verification(1);

        // Baseline 38e3c85b4: DispatchFailureClassifier.cs:1312-1326,1342-1358.
        Assert.True(SandboxCommitBlockedEvidence.IsSandboxCommitBlockedFailure(task, verification));
    }

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    [InlineData(AgentRole.Reviewer)]
    [InlineData((AgentRole)999)]
    public void Verification_ReadOnlyOrUnknownRole_ReturnsFalse(AgentRole role)
    {
        var task = new TaskSpec(TaskId.New(), "Read-only or unknown role", role);
        var verification = Verification(1);

        // Baseline 38e3c85b4: DispatchFailureClassifier.cs:1314-1317,1338-1340.
        Assert.False(SandboxCommitBlockedEvidence.IsSandboxCommitBlockedFailure(task, verification));
    }

    [Fact]
    public void Verification_SucceededDespiteCommitBlock_ReturnsFalse()
    {
        var task = new TaskSpec(TaskId.New(), "Developer task", AgentRole.Developer);
        var verification = Verification(0);

        // Baseline 38e3c85b4: DispatchFailureClassifier.cs:1314-1317.
        Assert.False(SandboxCommitBlockedEvidence.IsSandboxCommitBlockedFailure(task, verification));
    }

    [Theory]
    [InlineData(AgentRole.Developer, 1, UsefulOutput, CommitBlocked, true)]
    [InlineData(AgentRole.Developer, 0, UsefulOutput, CommitBlocked, false)]
    [InlineData(AgentRole.Reviewer, 1, UsefulOutput, CommitBlocked, false)]
    [InlineData((AgentRole)999, 1, UsefulOutput, CommitBlocked, false)]
    [InlineData(AgentRole.Developer, 1, "Starting up...", CommitBlocked, false)]
    [InlineData(AgentRole.Developer, 1, UsefulOutput, "error CS0103", false)]
    [InlineData(AgentRole.Developer, 1, UsefulOutput, ".git: Permission denied", true)]
    [InlineData(AgentRole.Developer, 1, UsefulOutput + "\nblocked on committing", "", true)]
    [InlineData(AgentRole.Developer, 1, UsefulOutput, "INDEX.LOCK", true)]
    [InlineData(AgentRole.Developer, 1, "", UsefulOutput + "\n" + CommitBlocked, false)]
    public void DirectStreams_EvidenceAndEligibility_PreserveBaseline(
        AgentRole role, int exitCode, string stdout, string stderr, bool expected)
    {
        // Baseline 38e3c85b4: DispatchFailureClassifier.cs:1329-1358.
        // Useful-work evidence must come from stdout in this overload, even when stderr has both markers.
        Assert.Equal(expected, SandboxCommitBlockedEvidence.IsSandboxCommitBlockedFailure(
            role, exitCode, stdout, stderr));
    }

    private static TaskVerificationRecord Verification(int exitCode) =>
        new("codex exec", "C:\\repo", exitCode, UsefulOutput, CommitBlocked, CompletedAt);
}
