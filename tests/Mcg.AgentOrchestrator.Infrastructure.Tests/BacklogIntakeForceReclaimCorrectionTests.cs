using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: isolated repositories/stores and AsyncLocal console capture; no global hooks or environment changes.
public sealed class BacklogIntakeForceReclaimCorrectionTests : CliCommandTestBase
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Correction_MismatchedCancelledOwners_TransfersClaimsAndReusesSuccessors(bool batch, bool recorded)
    {
        var workspace = await PrepareWorkspace();
        var owners = new List<(BacklogItem Item, GoalId Owner)>();
        owners.Add(await PrepareOwner(workspace, "Correct first intake pipeline", recorded));
        if (batch)
            owners.Add(await PrepareOwner(workspace, "Correct second intake pipeline", recorded));
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var claims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        var before = await repository.LoadAsync();
        var originalClaims = owners.ToDictionary(pair => pair.Item.Id,
            pair => claims.ResolveClaim(before, pair.Item.Id)!);
        Assert.All(owners, pair =>
        {
            var facts = GoalReplacementEvidence.Capture(workspace, before.GetGoal(pair.Owner));
            Assert.Equal(GoalStatus.Cancelled, facts.Status);
            Assert.False(facts.HasWork);
            Assert.True(facts.IsGitEvidenceAvailable);
            Assert.Equal(recorded, facts.IsRecorded);
        });
        string[] command = ["backlog-intake", .. owners.Select(pair => pair.Item.Id),
            "--force-reclaim", "--create-goal", "--pipeline", "scout", "--backlog-coverage", "full"];

        var output = Execute(workspace, command);

        Assert.Contains("GOAL_REPLACE_SUCCEEDED", output, StringComparison.Ordinal);
        var restored = await repository.LoadAsync();
        Assert.Equal(owners.Count * 2, restored.Goals.Count);
        var transferredClaims = owners.ToDictionary(pair => pair.Item.Id,
            pair => claims.ResolveClaim(restored, pair.Item.Id)!);
        foreach (var (item, ownerId) in owners)
        {
            var claim = transferredClaims[item.Id];
            Assert.NotEqual(ownerId.Value, claim.OwnerGoalId);
            Assert.Equal(originalClaims[item.Id].Version + 1, claim.Version);
            Assert.Equal(SourceBacklogCoverage.Slice, claim.Coverage);
            var successor = restored.GetGoal(new GoalId(claim.OwnerGoalId));
            Assert.Equal([AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
                successor.Tasks.Select(task => task.RequiredRole));
            Assert.All(successor.Tasks, task => Assert.Null(task.LastDispatch));
            var predecessor = restored.GetGoal(ownerId);
            Assert.Equal(GoalStatus.Cancelled, predecessor.Status);
            Assert.Contains(predecessor.Timeline, entry => entry.Message.Contains($"replaced by goal {successor.Id.Value}", StringComparison.Ordinal));
            Assert.Contains(successor.Timeline, entry => entry.Message.Contains($"inherited from goal {ownerId.Value}", StringComparison.Ordinal));
            var lineage = Assert.Single(claims.ListLineage(item.Id));
            Assert.Equal(ownerId.Value, lineage.PredecessorGoalId);
            Assert.Equal(successor.Id.Value, lineage.SuccessorGoalId);
            var audit = claims.FindAudit(lineage.RequestId);
            Assert.NotNull(audit);
            Assert.Equal(GoalReplacementOutcome.Succeeded, audit.Outcome);
            Assert.Equal(ownerId.Value, audit.PredecessorGoalId);
            Assert.Equal(successor.Id.Value, audit.SuccessorGoalId);
            Assert.Equal(recorded, audit.EligibilityFacts.IsRecorded);
            Assert.Equal(ownerId.Value, new BacklogIntakeRecordStore(workspace.SqliteStatePath).Get(item.Id)!.GoalId);
        }

        var repeatOutput = Execute(workspace, command);

        var repeated = await repository.LoadAsync();
        Assert.Equal(restored.Goals.Count, repeated.Goals.Count);
        Assert.DoesNotContain("GOAL_REPLACE_SUCCEEDED", repeatOutput, StringComparison.Ordinal);
        foreach (var (item, _) in owners)
        {
            Assert.Equal(transferredClaims[item.Id], claims.ResolveClaim(repeated, item.Id));
            Assert.Contains(transferredClaims[item.Id].OwnerGoalId[..8], repeatOutput, StringComparison.Ordinal);
            Assert.Single(claims.ListLineage(item.Id));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Batch_MixedCreationAndCorrection_PreservesCommittedPolicyDecisions(bool correctionFirst)
    {
        var workspace = await PrepareWorkspace();
        var (item, ownerId) = await PrepareOwner(workspace, "Correct mixed batch pipeline", true);
        var fresh = await new BacklogStore(workspace.BacklogStorePath).AddAsync("Create fresh mixed batch goal");
        var ids = correctionFirst ? new[] { item.Id, fresh.Id } : new[] { fresh.Id, item.Id };

        Execute(workspace, ["backlog-intake", .. ids, "--force-reclaim", "--create-goal",
            "--pipeline", "scout", "--backlog-coverage", "slice"]);

        var restored = await CreateMigratedStateRepository(workspace.SqliteStatePath).LoadAsync();
        Assert.Equal(3, restored.Goals.Count);
        var claims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
        var successorId = claims.ResolveClaim(restored, item.Id)!.OwnerGoalId;
        Assert.NotEqual(ownerId.Value, successorId);
        Assert.Contains(restored.GetGoal(ownerId).Timeline,
            entry => entry.Message.Contains($"replaced by goal {successorId}", StringComparison.Ordinal));
        Assert.Contains(restored.GetGoal(new GoalId(successorId)).Timeline,
            entry => entry.Message.Contains($"inherited from goal {ownerId.Value}", StringComparison.Ordinal));
        Assert.NotNull(claims.ResolveClaim(restored, fresh.Id));
    }

    [Fact]
    public async Task Intake_WithoutForceReclaim_PreservesPipelineMismatchRefusal()
    {
        var workspace = await PrepareWorkspace();
        var (item, ownerId) = await PrepareOwner(workspace, "Keep ordinary mismatch refusal", false);

        var error = Assert.Throws<InvalidOperationException>(() => Execute(workspace,
            ["backlog-intake", item.Id, "--create-goal", "--pipeline", "scout", "--backlog-coverage", "slice"]));

        Assert.Contains("does not match the explicitly requested intake pipeline", error.Message, StringComparison.Ordinal);
        var restored = await CreateMigratedStateRepository(workspace.SqliteStatePath).LoadAsync();
        Assert.Equal(ownerId, Assert.Single(restored.Goals).Id);
        Assert.Empty(new SourceBacklogClaimStore(workspace.SqliteStatePath).ListLineage(item.Id));
    }

    [Theory]
    [InlineData(false, "running", "running,dispatch", GoalReplacementOutcome.ProtectedOwner)]
    [InlineData(true, "running", "running,dispatch", GoalReplacementOutcome.ProtectedOwner)]
    [InlineData(false, "workspace", "workspace", GoalReplacementOutcome.IneligibleDisposition)]
    [InlineData(true, "workspace", "workspace", GoalReplacementOutcome.IneligibleDisposition)]
    [InlineData(false, "branch", "branch", GoalReplacementOutcome.IneligibleDisposition)]
    [InlineData(true, "branch", "branch", GoalReplacementOutcome.IneligibleDisposition)]
    [InlineData(false, "branch-delta", "branch,repository-delta", GoalReplacementOutcome.IneligibleDisposition)]
    [InlineData(true, "branch-delta", "branch,repository-delta", GoalReplacementOutcome.IneligibleDisposition)]
    [InlineData(false, "committed", "workspace,branch,repository-delta", GoalReplacementOutcome.IneligibleDisposition)]
    [InlineData(true, "committed", "workspace,branch,repository-delta", GoalReplacementOutcome.IneligibleDisposition)]
    [InlineData(false, "landing", "landed", GoalReplacementOutcome.ProtectedOwner)]
    [InlineData(true, "landing", "landed", GoalReplacementOutcome.ProtectedOwner)]
    [InlineData(false, "merged", "landed,merged,recorded", GoalReplacementOutcome.ProtectedOwner)]
    [InlineData(true, "merged", "landed,merged,recorded", GoalReplacementOutcome.ProtectedOwner)]
    public async Task Correction_ProtectedOwner_RefusesWithoutMutation(
        bool batch, string protection, string blockers, GoalReplacementOutcome outcome)
    {
        var workspace = await PrepareWorkspace();
        var (item, ownerId) = await PrepareOwner(workspace, "Protect intake owner", false, protection);
        try
        {
            var ids = new List<string> { item.Id };
            if (batch)
                ids.Add((await PrepareOwner(workspace, "Keep later batch owner", false)).Item.Id);
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var claims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
            var before = await repository.LoadAsync();
            var originalClaims = ids.ToDictionary(id => id, id => claims.ResolveClaim(before, id)!);
            var facts = GoalReplacementEvidence.Capture(workspace, before.GetGoal(ownerId));
            Assert.True(facts.IsGitEvidenceAvailable);
            Assert.Equal(blockers.Split(','), facts.ListCorrectionBlockers());
            if (protection == "workspace")
            {
                var worktree = GoalWorktrees.WorktreePath(workspace.ExecutionDirectory, ownerId);
                Assert.Contains("dirty-owner.txt", RunGitOutput(worktree, "status", "--porcelain"), StringComparison.Ordinal);
            }

            var error = Assert.Throws<InvalidOperationException>(() => Execute(workspace,
                ["backlog-intake", .. ids, "--force-reclaim", "--create-goal", "--pipeline", "scout",
                    "--backlog-coverage", "slice"]));

            Assert.Contains($"BACKLOG_INTAKE_FORCE_RECLAIM_REFUSED outcome={outcome}", error.Message, StringComparison.Ordinal);
            Assert.Contains($"owner={ownerId.Value}", error.Message, StringComparison.Ordinal);
            Assert.Contains($"refused_by={blockers}", error.Message, StringComparison.Ordinal);
            var restored = await repository.LoadAsync();
            Assert.Equal(before.Goals.Count, restored.Goals.Count);
            foreach (var id in ids)
            {
                Assert.Equal(originalClaims[id], claims.ResolveClaim(restored, id));
                Assert.Empty(claims.ListLineage(id));
            }
        }
        finally
        {
            CleanupAcceptanceRepository(workspace.ExecutionDirectory, ownerId);
        }
    }

    [Theory]
    [InlineData("dispatch", "recorded,dispatch")]
    [InlineData("branch", "recorded,branch")]
    public async Task RecordedWorkOwner_BothCommands_RefuseWithoutMutation(string protection, string blockers)
    {
        var workspace = await PrepareWorkspace();
        var (item, ownerId) = await PrepareOwner(workspace, "Keep recorded work owner", true, protection);
        try
        {
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var claims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
            var before = await repository.LoadAsync();
            var originalClaim = claims.ResolveClaim(before, item.Id)!;
            var facts = GoalReplacementEvidence.Capture(workspace, before.GetGoal(ownerId));
            Assert.True(facts.IsRecorded);
            Assert.True(facts.HasWork);
            Assert.True(facts.IsProtected);
            Assert.True(facts.IsGitEvidenceAvailable);
            Assert.Equal(blockers.Split(','), facts.ListCorrectionBlockers());

            var intakeError = Assert.Throws<InvalidOperationException>(() => Execute(workspace,
                ["backlog-intake", item.Id, "--force-reclaim", "--create-goal", "--pipeline", "scout",
                    "--backlog-coverage", "slice"]));

            Assert.Contains("BACKLOG_INTAKE_FORCE_RECLAIM_REFUSED outcome=ProtectedOwner", intakeError.Message, StringComparison.Ordinal);
            Assert.Contains($"refused_by={blockers}", intakeError.Message, StringComparison.Ordinal);
            var afterIntake = await repository.LoadAsync();
            Assert.Single(afterIntake.Goals);
            Assert.Equal(originalClaim, claims.ResolveClaim(afterIntake, item.Id));

            var requestId = Guid.NewGuid();
            var replacementCommand = await PrepareReplacementCommand(workspace, ownerId, requestId);
            var replaceError = Assert.Throws<InvalidOperationException>(() => Execute(workspace, replacementCommand));

            Assert.Contains("GOAL_REPLACE_PROTECTED_OWNER", replaceError.Message, StringComparison.Ordinal);
            Assert.Contains("protected_by=recorded", replaceError.Message, StringComparison.Ordinal);
            var audit = claims.FindAudit(requestId);
            Assert.NotNull(audit);
            Assert.Equal(GoalReplacementDisposition.ZeroWorkCorrection, audit.Disposition);
            Assert.Equal(GoalReplacementOutcome.ProtectedOwner, audit.Outcome);
            Assert.Equal(facts, audit.EligibilityFacts);
            var afterReplacement = await repository.LoadAsync();
            Assert.Single(afterReplacement.Goals);
            Assert.Equal(originalClaim, claims.ResolveClaim(afterReplacement, item.Id));
            Assert.Empty(claims.ListLineage(item.Id));
        }
        finally
        {
            CleanupAcceptanceRepository(workspace.ExecutionDirectory, ownerId);
        }
    }

    [Fact]
    public async Task Correction_ClaimChangesBeforeTransfer_RefusesStaleVersion()
    {
        var workspace = await PrepareWorkspace();
        var (item, ownerId) = await PrepareOwner(workspace, "Race intake correction", false);
        try
        {
            var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
            var claims = new SourceBacklogClaimStore(workspace.SqliteStatePath);
            var before = await repository.LoadAsync();
            var originalClaim = claims.ResolveClaim(before, item.Id)!;
            var facts = GoalReplacementEvidence.Capture(workspace, before.GetGoal(ownerId));
            Assert.Empty(facts.ListCorrectionBlockers());
            var winnerRequest = Guid.NewGuid();
            var winnerCommand = await PrepareReplacementCommand(workspace, ownerId, winnerRequest);
            GoalId? attemptedSuccessor = null;
            SourceBacklogClaimSnapshot? winningClaim = null;
            using var hook = GoalCreationSideEffectDelivery.UseBeforeStateCommit(goalId =>
            {
                if (attemptedSuccessor is not null)
                    return;
                attemptedSuccessor = goalId;
                Assert.Equal(originalClaim, claims.ResolveClaim(repository.LoadAsync().GetAwaiter().GetResult(), item.Id));
                Assert.Contains("GOAL_REPLACE_SUCCEEDED", Execute(workspace, winnerCommand), StringComparison.Ordinal);
                winningClaim = claims.ResolveClaim(repository.LoadAsync().GetAwaiter().GetResult(), item.Id);
                Assert.NotNull(winningClaim);
                Assert.Equal(originalClaim.Version + 1, winningClaim.Version);
                Assert.NotEqual(ownerId.Value, winningClaim.OwnerGoalId);
            });

            var conflict = Assert.Throws<SourceBacklogClaimConflictException>(() => Execute(workspace,
                ["backlog-intake", item.Id, "--force-reclaim", "--create-goal", "--pipeline", "scout",
                    "--backlog-coverage", "slice"]));

            Assert.NotNull(attemptedSuccessor);
            Assert.NotNull(winningClaim);
            Assert.Equal(winningClaim.OwnerGoalId, conflict.ObservedOwnerGoalId);
            Assert.Equal(winningClaim.Version, conflict.ObservedVersion);
            Assert.NotEqual(attemptedSuccessor.Value, winningClaim.OwnerGoalId);
            var restored = await repository.LoadAsync();
            Assert.Equal(winningClaim, claims.ResolveClaim(restored, item.Id));
            var lineage = Assert.Single(claims.ListLineage(item.Id));
            Assert.Equal(winnerRequest, lineage.RequestId);
            Assert.Equal(winningClaim.OwnerGoalId, lineage.SuccessorGoalId);
            Assert.Equal(GoalReplacementOutcome.Succeeded, claims.FindAudit(winnerRequest)!.Outcome);
        }
        finally
        {
            CleanupAcceptanceRepository(workspace.ExecutionDirectory, ownerId);
        }
    }

    private static async Task<string[]> PrepareReplacementCommand(
        OrchestratorWorkspace workspace, GoalId ownerId, Guid requestId)
    {
        var brief = Path.Combine(workspace.OrchestratorDirectory, "replacement-brief.md");
        var reason = Path.Combine(workspace.OrchestratorDirectory, "replacement-reason.md");
        await File.WriteAllTextAsync(brief, "Correct the cancelled zero-work intake pipeline.");
        await File.WriteAllTextAsync(reason, "Use the requested pipeline for the successor.");
        return ["goal-replace", ownerId.Value, "--brief-file", brief, "--reason-file", reason,
            "--request-id", requestId.ToString(), "--disposition", "zero-work-correction",
            "--confirm-goal-replace", "--pipeline", "scout"];
    }

    private async Task<OrchestratorWorkspace> PrepareWorkspace()
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        await File.WriteAllTextAsync(Path.Combine(root, ".gitignore"), ".agents/\n.orchestrator/\n");
        RunGit(root, "add", ".gitignore");
        RunGit(root, "commit", "-m", "Ignore fixture state");
        return workspace;
    }

    private async Task<(BacklogItem Item, GoalId Owner)> PrepareOwner(
        OrchestratorWorkspace workspace, string title, bool recorded, string? protection = null)
    {
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync(title);
        Execute(workspace, ["backlog-intake", item.Id, "--create-goal", "--pipeline", "developer-reviewer",
            "--backlog-coverage", "slice"]);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = await repository.LoadAsync();
        var owner = Assert.Single(kernel.Goals, goal => goal.SourceBacklogItemId == item.Id);
        Assert.Equal([AgentRole.Developer, AgentRole.Reviewer], owner.Tasks.Select(task => task.RequiredRole));
        if (protection is "running" or "dispatch")
        {
            var task = owner.Tasks.First();
            kernel.RecordTaskDispatch(owner.Id, task.Id,
                new TaskDispatchRecord("test-worker", "fixture-command", workspace.ExecutionDirectory, DateTimeOffset.UtcNow));
            if (protection == "running")
                kernel.RecordTaskProcessStarted(owner.Id, task.Id, new TaskProcessRecord(
                    12345, "fixture-command", workspace.ExecutionDirectory,
                    Path.Combine(workspace.OrchestratorDirectory, "stdout.log"),
                    Path.Combine(workspace.OrchestratorDirectory, "stderr.log"),
                    Path.Combine(workspace.OrchestratorDirectory, "exit.json"), DateTimeOffset.UtcNow, null, null));
        }
        kernel.CancelGoal(owner.Id, "Correct the zero-work intake pipeline.", allowLiveDispatches: true);
        await repository.SaveAsync(kernel);
        if (recorded)
            GoalOperationJournal.Completed(workspace.ExecutionDirectory, owner, "conductor:record", "Record fixture");
        var root = workspace.ExecutionDirectory;
        switch (protection)
        {
            case "workspace":
                var worktree = GoalWorktrees.WorktreePath(root, owner.Id);
                Directory.CreateDirectory(Path.GetDirectoryName(worktree)!);
                RunGit(root, "worktree", "add", "--detach", worktree, "HEAD");
                await File.WriteAllTextAsync(Path.Combine(worktree, "dirty-owner.txt"), "Uncommitted owner work");
                break;
            case "branch":
                RunGit(root, "branch", GoalWorktrees.BranchName(owner.Id));
                break;
            case "branch-delta":
            case "committed":
                var committedWorktree = CommitGoalWork(root, owner.Id, "owner-work.txt", "Committed owner work", useTemplate: false);
                if (protection == "branch-delta")
                    RunGit(root, "worktree", "remove", committedWorktree);
                break;
            case "landing":
                GoalOperationJournal.RecordLandingIntent(root, owner, GoalWorktrees.BranchName(owner.Id), workspace.IntegrationBranch,
                    RunGitOutput(root, "rev-parse", "HEAD").Trim(), "test");
                break;
            case "merged":
                GoalOperationJournal.RecordTerminalDisposition(root, owner, new GoalTerminalDisposition(
                    GoalTerminalDispositionKind.Landed, "Merge evidence fixture", GoalTerminalDispositionSource.MergeEvidence,
                    RunGitOutput(root, "rev-parse", "HEAD").Trim()));
                break;
        }
        return (item, owner.Id);
    }

    private static string Execute(OrchestratorWorkspace workspace, string[] command)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        return CaptureConsole(() => CliPersistentStateRunner.ExecuteCommand(command,
            CreateMigratedStateRepository(workspace.SqliteStatePath), workspace, ref agents,
            new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));
    }
}
