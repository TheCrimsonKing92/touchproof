using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: live work is durable fixture data, never a real worker or machine PID probe.
public sealed class GoalBranchCommitRefusalTests
{
    [Theory]
    [InlineData("WorkerDispatchLive", false)]
    [InlineData("AcceptanceAttemptLive", false)]
    [InlineData("NothingStaged", false)]
    [InlineData("UnstagedTrackedChanges", false)]
    [InlineData("WorkerDispatchLive", true)]
    [InlineData("AcceptanceAttemptLive", true)]
    [InlineData("ResumeHasStagedChanges", true)]
    public void Preflight_RefusesNamedCondition_WithoutCommitIntentsOrStateMutation(string reason, bool resume)
    {
        using var fixture = new GoalBranchCommitTestFixture();
        if (reason != "NothingStaged") fixture.StageChange();
        switch (reason)
        {
            case "WorkerDispatchLive":
                var task = Assert.Single(fixture.Goal.Tasks);
                fixture.Kernel.RecordTaskDispatch(fixture.GoalId, task.Id,
                    new TaskDispatchRecord("codex-cli", "fixture worker", fixture.Worktree, DateTimeOffset.UnixEpoch));
                fixture.Kernel.RecordTaskProcessStarted(fixture.GoalId, task.Id, new TaskProcessRecord(
                    900001, "fixture worker", fixture.Worktree, Path.Combine(fixture.Root, "worker.out"),
                    Path.Combine(fixture.Root, "worker.err"), Path.Combine(fixture.Root, "worker.exit"),
                    DateTimeOffset.UnixEpoch, null, null));
                fixture.Save();
                Assert.True(task.LastProcess!.IsRunning);
                break;
            case "AcceptanceAttemptLive":
                var directory = Path.Combine(fixture.Workspace.OrchestratorDirectory, "acceptance-gate-attempts", fixture.GoalId.Value);
                Directory.CreateDirectory(directory);
                var attempt = new ConductorParallelAcceptanceAttempt("live", fixture.GoalId.Value, fixture.GoalId.Value[..8],
                    0, fixture.Head, fixture.PreviousHead, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0,
                    ConductorParallelAcceptanceAttemptOutcome.Running, Path.Combine(directory, "out"),
                    Path.Combine(directory, "err"), Path.Combine(directory, "exit"), Path.Combine(directory, "heartbeat"),
                    Path.Combine(directory, "result"), Path.Combine(directory, "live.attempt.json"), ExecutionProtocol: "in-process");
                File.WriteAllText(attempt.MetadataPath, JsonSerializer.Serialize(attempt));
                Assert.True(GoalLiveAcceptancePredicate.ForWorkspace(fixture.Workspace)(fixture.GoalId));
                break;
            case "UnstagedTrackedChanges":
                File.WriteAllText(Path.Combine(fixture.Worktree, "seed.txt"), "unstaged edit\n");
                Assert.Equal("seed.txt", fixture.Git("diff", "--name-only"));
                break;
        }
        var beforeState = fixture.StateJson();
        var beforeStatus = fixture.Git("status", "--porcelain=v1");
        Assert.Empty(fixture.Intents());

        var result = fixture.Run(resume);

        Assert.Equal(1, result.ExitCode);
        Assert.Contains($"Error: {reason}:", result.Error);
        Assert.DoesNotContain("New head:", result.Output);
        Assert.Equal(fixture.PreviousHead, fixture.Head);
        Assert.Equal(beforeStatus, fixture.Git("status", "--porcelain=v1"));
        Assert.Empty(fixture.Intents());
        Assert.Equal(beforeState, fixture.StateJson());
    }
}
