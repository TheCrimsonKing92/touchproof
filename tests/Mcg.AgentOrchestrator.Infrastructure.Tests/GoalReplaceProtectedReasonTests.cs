using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated repositories/stores, AsyncLocal console capture, no global hooks or environment changes.
public sealed class GoalReplaceProtectedReasonTests : CliCommandTestBase
{
    private const string RetryNext = "next: stop the live dispatch and retry after the next conductor tick";
    private const string LandedNext = "next: the predecessor's work is on main; file a new goal instead";

    [Theory]
    [InlineData("running", "running", RetryNext)]
    [InlineData("landed", "landed", LandedNext)]
    [InlineData("merged", "landed,merged,recorded", LandedNext)]
    [InlineData("recorded", "recorded", null)]
    [InlineData("running-recorded", "running,recorded", null)]
    [InlineData("running-landed", "running,landed", LandedNext)]
    [InlineData("unavailable", "git-evidence-unavailable", null)]
    [InlineData("active", "none", null)]
    public async Task ProtectedReplacement_NamesCapturedProtectionsAndAdviceIncludingReplay(
        string scenario, string protections, string? next)
    {
        var (workspace, predecessor, command, requestId) = await PrepareReplacement(scenario);
        var evidenceLease = scenario == "unavailable"
            ? new ReconcileSweepRemediationStore(SetUpStateDatabase(workspace.SqliteStatePath)).TryAcquireAcceptanceLease(
                predecessor.Id.Value, $"goal-evidence:test:{Guid.NewGuid():N}", TimeSpan.FromMinutes(30))
            : null;
        if (scenario == "unavailable")
            Assert.NotNull(evidenceLease);

        InvalidOperationException? first = null;
        string firstOutput;
        try
        {
            firstOutput = CaptureConsole(() =>
                first = Assert.Throws<InvalidOperationException>(() => ExecuteReplacement(workspace, command)));
        }
        finally
        {
            evidenceLease?.Dispose();
        }

        Assert.NotNull(first);
        AssertProtectionMessage(first.Message, protections, next);
        var audit = new SourceBacklogClaimStore(workspace.SqliteStatePath).FindAudit(requestId);
        Assert.NotNull(audit);
        Assert.Equal(GoalReplacementOutcome.ProtectedOwner, audit.Outcome);
        var reason = scenario == "unavailable" ? "evidence-mutation-active" : "protected-owner";
        Assert.Equal($"{reason} protected_by={protections}", audit.FailureCode);
        Assert.DoesNotContain("next:", audit.FailureCode, StringComparison.Ordinal);
        Assert.Equal(scenario == "active" ? GoalStatus.Active : GoalStatus.Cancelled, audit.OldStatus);
        Assert.Equal(protections == "none" ? [] : protections.Split(','), audit.EligibilityFacts.ListProtections());

        // The evidence lease is now gone; replay must still describe the stored refusal facts.
        InvalidOperationException? replay = null;
        var replayOutput = CaptureConsole(() =>
            replay = Assert.Throws<InvalidOperationException>(() => ExecuteReplacement(workspace, command)));
        Assert.NotNull(replay);
        AssertProtectionMessage(replay.Message, protections, next);
        Assert.Contains($"protected_by={protections}", replayOutput, StringComparison.Ordinal);
        if (scenario != "unavailable")
            Assert.Contains($"protected_by={protections}", firstOutput, StringComparison.Ordinal);

        var restored = await CreateMigratedStateRepository(workspace.SqliteStatePath).LoadAsync();
        Assert.Single(restored.Goals);
        Assert.Equal(predecessor.Id.Value,
            new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(restored, audit.BacklogItemId)!.OwnerGoalId);
    }

    [Fact]
    public async Task CancelledPredecessorWithUnlandedWork_StillTransfersClaim()
    {
        var (workspace, predecessor, command, requestId) = await PrepareReplacement("work");
        var before = GoalReplacementEvidence.Capture(workspace, predecessor);
        Assert.True(before.HasWork);
        Assert.False(before.IsProtected);
        Assert.Equal(GoalStatus.Cancelled, before.Status);

        var output = CaptureConsole(() => ExecuteReplacement(workspace, command));

        Assert.Contains("GOAL_REPLACE_SUCCEEDED", output, StringComparison.Ordinal);
        Assert.DoesNotContain("protected_by=", output, StringComparison.Ordinal);
        var audit = new SourceBacklogClaimStore(workspace.SqliteStatePath).FindAudit(requestId);
        Assert.NotNull(audit);
        Assert.Equal(GoalReplacementOutcome.Succeeded, audit.Outcome);
        Assert.Equal(GoalReplacementDisposition.SupersedeUnlandedAttempt, audit.Disposition);
        var restored = await CreateMigratedStateRepository(workspace.SqliteStatePath).LoadAsync();
        var successor = Assert.Single(restored.Goals, goal => goal.Id != predecessor.Id);
        Assert.Equal(successor.Id.Value, audit.SuccessorGoalId);
        Assert.Equal(successor.Id.Value,
            new SourceBacklogClaimStore(workspace.SqliteStatePath).ResolveClaim(restored, audit.BacklogItemId)!.OwnerGoalId);
    }

    private async Task<(OrchestratorWorkspace Workspace, Goal Predecessor, string[] Command, Guid RequestId)>
        PrepareReplacement(string scenario)
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".agents/\n.orchestrator/\n");
        RunGit(root, "add", ".gitignore");
        RunGit(root, "commit", "-m", "Ignore fixture state");
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Explain protected replacement");
        var kernel = new AgentOrchestratorKernel();
        var predecessor = kernel.CreateGoal("Predecessor with replacement evidence");
        var task = kernel.AddTask(predecessor.Id, AgentRole.Developer, "Implement predecessor work.");
        kernel.SetGoalSourceBacklogItemLink(predecessor.Id, item.Id, SourceBacklogCoverage.Full);
        kernel.ActivateGoal(predecessor.Id, AgentCatalog.Default().Agents);
        if (scenario.StartsWith("running", StringComparison.Ordinal))
        {
            kernel.RecordTaskDispatch(predecessor.Id, task.Id,
                new TaskDispatchRecord("test-worker", "fixture-command", root, DateTimeOffset.UtcNow));
            kernel.RecordTaskProcessStarted(predecessor.Id, task.Id, new TaskProcessRecord(
                12345, "fixture-command", root, Path.Combine(root, "stdout.log"),
                Path.Combine(root, "stderr.log"), Path.Combine(root, "exit.json"),
                DateTimeOffset.UtcNow, null, null));
            Assert.True(task.LastProcess!.IsRunning);
        }
        if (scenario != "active")
            kernel.CancelGoal(predecessor.Id, "Replace this attempt.", allowLiveDispatches: true);
        await CreateMigratedStateRepository(workspace.SqliteStatePath).SaveAsync(kernel);

        if (scenario is "landed" or "running-landed")
            GoalOperationJournal.RecordLandingIntent(root, predecessor, "goal/fixture", workspace.IntegrationBranch,
                RunGitOutput(root, "rev-parse", "HEAD").Trim(), "test");
        if (scenario == "merged")
            GoalOperationJournal.RecordTerminalDisposition(root, predecessor, new GoalTerminalDisposition(
                GoalTerminalDispositionKind.Landed, "Merge evidence fixture", GoalTerminalDispositionSource.MergeEvidence,
                RunGitOutput(root, "rev-parse", "HEAD").Trim()));
        if (scenario is "recorded" or "running-recorded")
            GoalOperationJournal.Completed(root, predecessor, "conductor:record", "Record fixture");
        if (scenario == "work")
            _ = CommitGoalWork(root, predecessor.Id, "replacement-work.txt", "Unlanded predecessor work");

        var brief = Path.Combine(root, "replacement-brief.md");
        var reason = Path.Combine(root, "replacement-reason.md");
        await File.WriteAllTextAsync(brief, "Update src/Mcg.AgentOrchestrator.App/Cli/CliCommandHelp.cs with a focused assertion.");
        await File.WriteAllTextAsync(reason, "Supersede the unlanded predecessor attempt.");
        var requestId = Guid.NewGuid();
        return (workspace, predecessor,
            ["goal-replace", predecessor.Id.Value, "--brief-file", brief, "--reason-file", reason,
                "--request-id", requestId.ToString(), "--disposition", "supersede-unlanded-attempt",
                "--confirm-goal-replace", "--pipeline", "scout"], requestId);
    }

    private static void ExecuteReplacement(OrchestratorWorkspace workspace, string[] command)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        CliPersistentStateRunner.ExecuteCommand(command, CreateMigratedStateRepository(workspace.SqliteStatePath),
            workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal);
    }

    private static void AssertProtectionMessage(string message, string protections, string? next)
    {
        Assert.Contains("GOAL_REPLACE_PROTECTED_OWNER", message, StringComparison.Ordinal);
        var lines = message.Split(Environment.NewLine);
        Assert.EndsWith($"protected_by={protections}", lines[0], StringComparison.Ordinal);
        Assert.Equal(1, message.Split("protected_by=", StringSplitOptions.None).Length - 1);
        if (next is null)
        {
            Assert.Single(lines);
            Assert.DoesNotContain("next:", message, StringComparison.Ordinal);
        }
        else
            Assert.Equal([lines[0], next], lines);
    }
}
