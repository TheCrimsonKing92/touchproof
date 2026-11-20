using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// The repositories, observers and console capture are scoped to each invocation.
public sealed class BacklogIntakeContinuationTests : CliCommandTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Intake_CurrentOwner_ReusesGoal(bool currentIntake)
    {
        var fixture = await Prepare(landed: false, recordOnly: currentIntake);
        var before = await ReadState(fixture);
        if (currentIntake)
        {
            Assert.Null(before.Claim);
            Assert.Equal("InProgress", before.Intake?.Status);
        }

        var output = Execute(fixture, force: false, pipeline: "developer-reviewer");

        Assert.Contains($"already has goal {fixture.Predecessor.Value[..8]}; no new goal created", output);
        AssertUnchanged(before, await ReadState(fixture));
    }

    [Fact]
    public async Task Intake_LandedSlice_CreatesScoutSuccessor()
    {
        var fixture = await Prepare();
        var before = await ReadState(fixture);

        var output = Execute(fixture);

        var after = await ReadState(fixture);
        Assert.Equal(2, after.Goals.Length);
        Assert.Contains(fixture.Predecessor, after.Goals);
        var successorId = Assert.Single(after.Goals, id => id != fixture.Predecessor);
        var successor = (await OpenRepository(fixture).LoadAsync()).GetGoal(successorId);
        Assert.Equal([AgentRole.Planner, AgentRole.Developer, AgentRole.Tester, AgentRole.Reviewer],
            successor.Tasks.Select(task => task.RequiredRole));
        Assert.Equal(successorId.Value, after.Claim.OwnerGoalId);
        Assert.Equal(SourceBacklogCoverage.Slice, after.Claim.Coverage);
        Assert.Equal(before.Claim.Version + 1, after.Claim.Version);
        var lineage = Assert.Single(after.Lineage);
        Assert.Equal(fixture.Item.Id, lineage.BacklogItemId);
        Assert.Equal(fixture.Predecessor.Value, lineage.PredecessorGoalId);
        Assert.Equal(successorId.Value, lineage.SuccessorGoalId);
        Assert.Equal(before.Claim.Version, lineage.ClaimVersionBefore);
        Assert.Equal(after.Claim.Version, lineage.ClaimVersionAfter);
        Assert.Equal(TimeSpan.Zero, lineage.TransferredAt.Offset);
        Assert.Contains($"predecessor={fixture.Predecessor.Value} successor={successorId.Value}", output);
        Assert.Equal(successorId.Value, after.Intake?.GoalId);
        Assert.Equal("GoalCreated", after.Intake?.Status);
    }

    [Fact]
    public async Task Intake_WithoutForce_ReusesLandedOwner()
    {
        var fixture = await Prepare(pipeline: "scout");
        var before = await ReadState(fixture);

        var output = Execute(fixture, force: false);

        Assert.Contains($"already has goal {fixture.Predecessor.Value[..8]}; no new goal created", output);
        AssertUnchanged(before, await ReadState(fixture));
    }

    [Fact]
    public async Task Intake_ConcurrentCalls_TransfersOnce()
    {
        var fixture = await Prepare();
        var before = await ReadState(fixture);
        using var boundary = new Barrier(participantCount: 2);
        var hooks = new BacklogIntakeContinuationHooks(BeforeFirstWrite: () =>
        {
            Assert.True(boundary.SignalAndWait(TimeSpan.FromSeconds(30)),
                "Both calls did not reach the evaluated continuation boundary.");
        });

        Task<string> Attempt() => Task.Run(() =>
        {
            try { return Execute(fixture, hooks: hooks); }
            catch (BacklogIntakeContinuationException error) { return error.Message; }
        });
        var results = await Task.WhenAll(Attempt(), Attempt());

        Assert.Single(results, result => result.Contains("BACKLOG_INTAKE_CONTINUED", StringComparison.Ordinal));
        Assert.Single(results, result => result.Contains("reason=SnapshotChanged", StringComparison.Ordinal));
        var after = await ReadState(fixture);
        Assert.Equal(2, after.Goals.Length);
        Assert.Single(after.Lineage);
        Assert.Equal(before.Claim.Version + 1, after.Claim.Version);
        Assert.Equal(after.Claim.OwnerGoalId, after.Intake?.GoalId);
    }

    [Theory]
    [InlineData("Prepared", false, false)]
    [InlineData("Transferred", true, false)]
    [InlineData("Rearmed", true, true)]
    public async Task Intake_InterruptedPhase_ResumesSuccessor(string phase, bool transferred, bool rearmed)
    {
        var fixture = await Prepare();
        var before = await ReadState(fixture);
        var hooks = new BacklogIntakeContinuationHooks(AfterPhaseCommit: reservation =>
        {
            if (reservation.Phase.ToString() == phase)
                throw new ContinuationInterruptedException();
        });

        Assert.Throws<ContinuationInterruptedException>(() => Execute(fixture, hooks: hooks));

        var interrupted = await ReadState(fixture);
        Assert.Equal(2, interrupted.Goals.Length);
        var draftId = Assert.Single(interrupted.Goals, id => id != fixture.Predecessor);
        var draft = (await OpenRepository(fixture).LoadAsync()).GetGoal(draftId);
        Assert.Equal(GoalStatus.Draft, draft.Status);
        Assert.Equal(phase, interrupted.Reservation?.Phase.ToString());
        Assert.Equal(draftId.Value, interrupted.Reservation?.SuccessorId);
        Assert.Equal(transferred ? 1 : 0, interrupted.Lineage.Count);
        Assert.Equal(transferred ? draftId.Value : fixture.Predecessor.Value, interrupted.Claim.OwnerGoalId);
        Assert.Equal(rearmed ? "InProgress" : "GoalCreated", interrupted.Intake?.Status);
        Assert.Equal(rearmed ? null : fixture.Predecessor.Value, interrupted.Intake?.GoalId);

        var output = Execute(fixture);

        var after = await ReadState(fixture);
        Assert.Contains($"successor={draftId.Value}", output);
        Assert.Equal(interrupted.Goals, after.Goals);
        Assert.Equal(draftId.Value, Assert.Single(after.Lineage).SuccessorGoalId);
        Assert.Equal(before.Claim.Version + 1, after.Claim.Version);
        Assert.Equal(draftId.Value, after.Intake?.GoalId);
        Assert.Equal("GoalCreated", after.Intake?.Status);
        Assert.Null(after.Reservation);
        Assert.Equal(GoalStatus.Active, (await OpenRepository(fixture).LoadAsync()).GetGoal(draftId).Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("f99f99f99f99f99f99f99f99f99f99f9")]
    public async Task Intake_StaleRecord_ValidatesOwnership(string? danglingId)
    {
        var fixture = await Prepare();
        ExecuteSql(fixture, "UPDATE backlog_intake_records SET goal_id = $goal, status = 'InProgress'", danglingId);
        var before = await ReadState(fixture);
        var evaluated = false;
        var hooks = new BacklogIntakeContinuationHooks(BeforeFirstWrite: () => evaluated = true);

        var refusal = Assert.Throws<BacklogIntakeContinuationException>(() => Execute(fixture, hooks: hooks));

        Assert.True(evaluated);
        Assert.Equal(BacklogIntakeContinuationRefusal.StaleIntake, refusal.Reason);
        AssertUnchanged(before, await ReadState(fixture));
    }

    [Theory]
    [InlineData(true, "full")]
    [InlineData(false, "slice")]
    public async Task Intake_IneligibleOwner_RefusesTransfer(bool landed, string coverage)
    {
        var fixture = await Prepare(landed: landed, coverage: coverage);
        var before = await ReadState(fixture);

        var refusal = Assert.Throws<BacklogIntakeContinuationException>(() => Execute(fixture));

        Assert.Equal(landed ? BacklogIntakeContinuationRefusal.FullCoverage : BacklogIntakeContinuationRefusal.OwnerActive,
            refusal.Reason);
        AssertUnchanged(before, await ReadState(fixture));
    }

    [Theory]
    [InlineData("owner", "UPDATE source_backlog_claims SET owner_goal_id = $goal")]
    [InlineData("version", "UPDATE source_backlog_claims SET version = version + 1")]
    [InlineData("coverage", "UPDATE source_backlog_claims SET coverage = 'Full'")]
    [InlineData("landing", null)]
    [InlineData("intake", "UPDATE backlog_intake_records SET status = 'InProgress'")]
    public async Task Intake_ChangedSnapshot_RefusesWithoutWriting(string changedFact, string? sql)
    {
        var fixture = await Prepare();
        StoreState? competingState = null;
        var hooks = new BacklogIntakeContinuationHooks(BeforeFirstWrite: () =>
        {
            if (sql is not null)
                ExecuteSql(fixture, sql, GoalId.New().Value);
            else
                File.Delete(GoalOperationJournal.PathFor(fixture.Workspace.ExecutionDirectory, fixture.Predecessor));
            competingState = ReadState(fixture).GetAwaiter().GetResult();
        });

        var refusal = Assert.Throws<BacklogIntakeContinuationException>(() => Execute(fixture, hooks: hooks));

        Assert.Equal(BacklogIntakeContinuationRefusal.SnapshotChanged, refusal.Reason);
        Assert.True(competingState is not null, $"The controlled {changedFact} change was not applied.");
        AssertUnchanged(competingState!, await ReadState(fixture));
    }

    [Fact]
    public async Task Intake_LandedSlice_BypassesCorrection()
    {
        var fixture = await Prepare();

        var output = Execute(fixture);

        Assert.DoesNotContain("GOAL_REPLACE_SUCCEEDED", output);
        var claims = new SourceBacklogClaimStore(fixture.Workspace.SqliteStatePath);
        Assert.Empty(claims.ListLineage(fixture.Item.Id));
        Assert.Equal(fixture.Predecessor.Value, Assert.Single(claims.ListContinuationLineage(fixture.Item.Id)).PredecessorGoalId);
    }

    [Fact]
    public async Task Intake_KeyedReplay_ReportsCommittedSuccessor()
    {
        var fixture = await Prepare();
        const string key = "continue-ticket";

        var output = Execute(fixture, requestKey: key);
        var after = await ReadState(fixture);
        var replay = Execute(fixture, requestKey: key);

        Assert.Contains("BACKLOG_INTAKE_CONTINUED", output);
        Assert.Contains($"successor={after.Claim.OwnerGoalId}", replay);
        Assert.Equal(after.Claim.OwnerGoalId, new GoalIntakeRequestStore(fixture.Workspace.SqliteStatePath).Get(key)?.GoalId);
        AssertUnchanged(after, await ReadState(fixture));
    }

    private async Task<Fixture> Prepare(bool landed = true, string coverage = "slice", string pipeline = "developer-reviewer",
        bool recordOnly = false)
    {
        var root = CreateAcceptanceRepository();
        var workspace = CreateRefinedWorkspace(root);
        var backlog = new BacklogStore(workspace.BacklogStorePath);
        var item = await backlog.AddAsync("Continue the settings acceptance slice");
        var fixture = new Fixture(workspace, item, default);
        var repository = OpenRepository(fixture);
        if (recordOnly)
        {
            var standalone = new AgentOrchestratorKernel();
            var plan = GoalObjectivePlanner.Build(item.Title, pipelineOverride: GoalIntakePipeline.DeveloperReviewer);
            var linkedByIntake = GoalLifecycleCommands.CreateAndActivateGoal(standalone, AgentCatalog.Default().Agents, plan);
            await repository.SaveAsync(standalone);
            var records = new BacklogIntakeRecordStore(workspace.SqliteStatePath);
            Assert.Equal(BacklogIntakeReservationKind.Acquired, records.Reserve(item.Id, item.Title).Kind);
            records.MarkGoalCreated(item.Id, linkedByIntake.Id.Value);
            ExecuteSql(fixture, "UPDATE backlog_intake_records SET status = 'InProgress'");
            return fixture with { Predecessor = linkedByIntake.Id };
        }
        Execute(fixture, force: false, pipeline: pipeline, coverage: coverage);
        var kernel = await repository.LoadAsync();
        var owner = Assert.Single(kernel.Goals);
        if (landed)
        {
            foreach (var task in owner.Tasks)
                kernel.ReportTaskProgress(owner.Id, task.Id, WorkTaskStatus.Completed, "Fixture task complete.");
            var sha = RunGitOutput(root, "rev-parse", "HEAD").Trim();
            kernel.CompleteGoalFromMergeEvidence(owner.Id, sha, "Fixture predecessor landed.");
            GoalOperationJournal.Completed(root, owner, "conductor:land", "Fixture predecessor landed.");
            await repository.SaveAsync(kernel);
            Assert.True(await backlog.TryCloseByIdAsync(item.Id));
            await backlog.ReopenAsync(item.Id);
        }
        return fixture with { Predecessor = owner.Id };
    }

    private static SqliteOrchestratorStateRepository OpenRepository(Fixture fixture) =>
        CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath);

    private static string Execute(Fixture fixture, bool force = true, string pipeline = "scout", string coverage = "slice",
        BacklogIntakeContinuationHooks? hooks = null, string? requestKey = null)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        var providers = new InMemoryModelProviderRegistry([]);
        Goal? goal = null;
        var command = new List<string>
            { "backlog-intake", fixture.Item.Id, "--create-goal", "--pipeline", pipeline, "--backlog-coverage", coverage };
        if (force)
            command.Add("--force-reclaim");
        if (requestKey is not null)
            command.AddRange(["--request-key", requestKey]);
        var repository = OpenRepository(fixture);
        return CaptureConsole(() =>
        {
            if (hooks is null)
                CliPersistentStateRunner.ExecuteCommand(command, repository, fixture.Workspace,
                    ref agents, providers, ref profiles, ref goal);
            else
                CliBacklogIntakeExecution.Execute(command, repository, fixture.Workspace,
                    ref agents, providers, ref profiles, ref goal, hooks);
        });
    }

    private static async Task<StoreState> ReadState(Fixture fixture)
    {
        var path = fixture.Workspace.SqliteStatePath;
        var kernel = await OpenRepository(fixture).LoadAsync();
        var claims = new SourceBacklogClaimStore(path);
        var intake = new BacklogIntakeRecordStore(path);
        return new(kernel.Goals.Select(goal => goal.Id).OrderBy(id => id.Value, StringComparer.Ordinal).ToArray(),
            claims.ResolveClaim(kernel, fixture.Item.Id)!, intake.Get(fixture.Item.Id),
            claims.ListContinuationLineage(fixture.Item.Id), intake.GetContinuation(fixture.Item.Id));
    }

    private static void AssertUnchanged(StoreState before, StoreState after)
    {
        Assert.Equal(before.Goals, after.Goals);
        Assert.Equal(before.Claim, after.Claim);
        Assert.Equal(before.Intake, after.Intake);
        Assert.Equal(before.Lineage.ToArray(), after.Lineage.ToArray());
        Assert.Equal(before.Reservation, after.Reservation);
    }

    private static void ExecuteSql(Fixture fixture, string sql, string? value = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = fixture.Workspace.SqliteStatePath, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (sql.Contains("$goal", StringComparison.Ordinal))
            command.Parameters.AddWithValue("$goal", (object?)value ?? DBNull.Value);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private sealed record Fixture(OrchestratorWorkspace Workspace, BacklogItem Item, GoalId Predecessor);

    private sealed record StoreState(GoalId[] Goals, SourceBacklogClaimSnapshot Claim, BacklogIntakeRecord? Intake,
        IReadOnlyList<SourceBacklogContinuationLineage> Lineage, BacklogIntakeContinuationReservation? Reservation);

    private sealed class ContinuationInterruptedException : Exception;
}
