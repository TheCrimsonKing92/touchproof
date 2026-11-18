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
        OrchestratorWorkspace workspace, string title, bool recorded)
    {
        var item = await new BacklogStore(workspace.BacklogStorePath).AddAsync(title);
        Execute(workspace, ["backlog-intake", item.Id, "--create-goal", "--pipeline", "developer-reviewer",
            "--backlog-coverage", "slice"]);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        var kernel = await repository.LoadAsync();
        var owner = Assert.Single(kernel.Goals, goal => goal.SourceBacklogItemId == item.Id);
        Assert.Equal([AgentRole.Developer, AgentRole.Reviewer], owner.Tasks.Select(task => task.RequiredRole));
        kernel.CancelGoal(owner.Id, "Correct the zero-work intake pipeline.");
        await repository.SaveAsync(kernel);
        if (recorded)
            GoalOperationJournal.Completed(workspace.ExecutionDirectory, owner, "conductor:record", "Record fixture");
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
