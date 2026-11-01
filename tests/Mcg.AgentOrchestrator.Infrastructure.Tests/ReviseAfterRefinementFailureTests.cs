using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

// Parallel-safe: each case owns a migrated database, workspace and provider; CLI output
// uses the existing AsyncLocal console router. No process or environment hooks are changed.
public sealed class ReviseAfterRefinementFailureTests : CliCommandTestBase
{
    private const string OriginalBrief = "Original failure-only sentence.\n\n## Acceptance criteria\n\n1. Original criterion.";
    private const string RevisedBrief = "Revised recovery-only sentence.\n\n## Acceptance criteria\n\n1. Revised criterion.";
    private const string ProviderName = "revision-regression-refiner";
    private const string GoodResponse = """
        {"behavioralContract":"New contract","acceptanceCriteria":[{"text":"Revised criterion.","phase":"PreLanding","declared_index":1}],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
        """;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedRowBecomesPendingAndNextAttemptReadsRevisedBrief(bool joined)
    {
        var scenario = await CreateScenarioAsync();
        var badProvider = new RecordingProvider("""
            {"behavioralContract":"Old contract","acceptanceCriteria":[{"text":"Unmatched model criterion","phase":"PreLanding"}],"verificationClass":"TestVerifiable","decisions":[],"forks":[]}
            """);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessAsync(scenario, badProvider));
        Assert.StartsWith("SPEC_REFINEMENT_FAILED", failure.Message, StringComparison.Ordinal);
        Assert.Contains("failed rule: criterion count.", failure.Message, StringComparison.Ordinal);
        Assert.Equal(1, badProvider.Calls);
        scenario.Kernel = await scenario.Repository.LoadAsync();
        var failed = await scenario.Repository.GetOutboxStateAsync(scenario.MessageId);
        Assert.Equal(OrchestratorStateOutboxStatus.Failed, failed!.Status);
        Assert.Equal(failure.Message, failed.Detail);
        Assert.Null(ReadRow(scenario)![6]); // Released token plus a recent timestamp is not a live lease.
        Assert.NotNull(failed.ProcessingStartedAt);
        Assert.Equal(GoalRefinementReadiness.Failed, Readiness(scenario, failed));
        Assert.True(SpecRefinementFailureResetEvaluation.Evaluate(scenario.Workspace, scenario.Goal));
        SeedAttemptState(scenario, failed.Message.CreatedAt);
        var before = ReadRow(scenario)!;

        var output = await ReviseAsync(scenario, RevisedBrief, joined);

        Assert.Contains("refined-criteria=reset", output, StringComparison.Ordinal);
        Assert.DoesNotContain("refinement is in progress", output, StringComparison.Ordinal);
        var pending = await AssertPendingAsync(scenario, before);
        Assert.Equal(GoalRefinementReadiness.Pending, Readiness(scenario, pending));
        Assert.Null(SpecRefinementLaunchAttemptStore.ForWorkspace(scenario.Workspace).Get(scenario.GoalId));
        Assert.Null(SpecRefinementTransientRetryStore.ForWorkspace(scenario.Workspace).Get(scenario.GoalId));
        Assert.Equal(RevisedBrief, scenario.Goal.AuthoritativeBrief.Text);

        // The readiness gate must permit the retry before calling the executor; direct
        // ProcessAsync alone can claim a failed row and would miss the original defect.
        var goodProvider = new RecordingProvider(GoodResponse);
        var result = await ProcessAsync(scenario, goodProvider);
        Assert.True(result.Claimed);
        Assert.True(result.Attached);
        Assert.Equal(1, goodProvider.Calls);
        Assert.NotNull(goodProvider.Request);
        Assert.Contains(goodProvider.Request.Messages,
            message => message.Content.Contains(RevisedBrief, StringComparison.Ordinal));
        Assert.DoesNotContain(goodProvider.Request.Messages,
            message => message.Content.Contains("Original failure-only sentence.", StringComparison.Ordinal));
        var refined = (await scenario.Repository.LoadAsync()).GetGoal(scenario.GoalId);
        Assert.Equal(["Revised criterion."], refined.RefinedSpec!.AcceptanceCriteria);
        Assert.Equal(GoalRefinementReadiness.Ready,
            GoalRefinementGate.Evaluate(refined, await scenario.Repository.GetOutboxStateAsync(scenario.MessageId), new([])).Readiness);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LiveLeasePreservesRowAndActiveAttemptAndReportsInProgress(bool joined)
    {
        var scenario = await CreateScenarioAsync();
        // Fixed timestamps sit far from the expiry boundary; execution speed never
        // decides the verdict. These cases exercise the store's existing lease predicate.
        SetClaim(scenario, "2099-01-01T00:00:00.0000000+00:00");
        var processing = await scenario.Repository.GetOutboxStateAsync(scenario.MessageId);
        Assert.Equal(OrchestratorStateOutboxStatus.Processing, processing!.Status);
        SeedAttemptState(scenario, processing.Message.CreatedAt);
        var launchStore = SpecRefinementLaunchAttemptStore.ForWorkspace(scenario.Workspace);
        var retryStore = SpecRefinementTransientRetryStore.ForWorkspace(scenario.Workspace);
        var launch = launchStore.Get(scenario.GoalId);
        var retry = retryStore.Get(scenario.GoalId);
        Assert.NotNull(launch);
        Assert.NotNull(retry);
        Assert.True(SpecRefinementFailureResetEvaluation.Evaluate(scenario.Workspace, scenario.Goal));
        var before = ReadRow(scenario)!;

        var output = await ReviseAsync(scenario, RevisedBrief, joined);

        Assert.Contains("refinement is in progress", output, StringComparison.Ordinal);
        Assert.Equal(before, ReadRow(scenario));
        Assert.Equal(launch, launchStore.Get(scenario.GoalId));
        Assert.Equal(retry, retryStore.Get(scenario.GoalId));
        Assert.Equal(RevisedBrief, scenario.Goal.AuthoritativeBrief.Text);
        Assert.Equal(2, scenario.Goal.AuthoritativeBrief.Version);
        Assert.Equal(OrchestratorStateOutboxStatus.Processing,
            (await scenario.Repository.GetOutboxStateAsync(scenario.MessageId))!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredLeaseAndQuarantineAreClearedOnRevision(bool joined)
    {
        var scenario = await CreateScenarioAsync();
        SetClaim(scenario, "2000-01-01T00:00:00.0000000+00:00", quarantined: true);
        var before = ReadRow(scenario)!;
        Assert.All(before.Skip(4), value => Assert.NotNull(value));
        SeedAttemptState(scenario, (await scenario.Repository.GetOutboxStateAsync(scenario.MessageId))!.Message.CreatedAt);
        Assert.True(SpecRefinementFailureResetEvaluation.Evaluate(scenario.Workspace, scenario.Goal));

        var output = await ReviseAsync(scenario, RevisedBrief, joined);

        Assert.DoesNotContain("refinement is in progress", output, StringComparison.Ordinal);
        await AssertPendingAsync(scenario, before);
        Assert.Null(SpecRefinementLaunchAttemptStore.ForWorkspace(scenario.Workspace).Get(scenario.GoalId));
        Assert.Null(SpecRefinementTransientRetryStore.ForWorkspace(scenario.Workspace).Get(scenario.GoalId));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NonfailedGoalKeepsExistingRowOrAbsenceAndStoredSpec(bool hasRow, bool joined)
    {
        var scenario = await CreateScenarioAsync(hasRow);
        var spec = new RefinedSpec("Stable contract", ["Original criterion."], VerificationClass.TestVerifiable, [], [])
        {
            AcceptanceGateOwnedAcceptanceCriteria = ["Original criterion."],
            CriterionEvidenceDeclarations = [new("Original criterion.")]
        };
        scenario.Kernel.SetGoalRefinedSpec(scenario.GoalId, spec);
        await scenario.Repository.SaveAsync(scenario.Kernel);
        Assert.False(SpecRefinementFailureResetEvaluation.Evaluate(scenario.Workspace, scenario.Goal));
        var before = ReadRow(scenario);
        if (hasRow) Assert.NotNull(before);
        else Assert.Null(before);
        const string revisedProse = "Revised prose only.\n\n## Acceptance criteria\n\n1. Original criterion.";

        var output = await ReviseAsync(scenario, revisedProse, joined);

        Assert.Contains("refined-criteria=unchanged", output, StringComparison.Ordinal);
        Assert.DoesNotContain("refinement is in progress", output, StringComparison.Ordinal);
        Assert.Equal(before, ReadRow(scenario));
        Assert.Equal(hasRow ? 1 : 0,
            (await scenario.Repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind)).Count);
        Assert.Equal(revisedProse, scenario.Goal.AuthoritativeBrief.Text);
        Assert.Equal(spec.BehavioralContract, scenario.Goal.RefinedSpec!.BehavioralContract);
        Assert.Equal(spec.AcceptanceCriteria, scenario.Goal.RefinedSpec.AcceptanceCriteria);
        Assert.Equal(spec.CriterionEvidenceDeclarations, scenario.Goal.RefinedSpec.CriterionEvidenceDeclarations);
        Assert.Single(scenario.Goal.RefinedSpecVersions);
        Assert.Single(scenario.Goal.CriterionEvidenceObligations);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedLaunchWithMissingRowInsertsPendingWork(bool joined)
    {
        var scenario = await CreateScenarioAsync(hasRow: false);
        SeedAttemptState(scenario, new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Null(ReadRow(scenario));
        Assert.True(SpecRefinementFailureResetEvaluation.Evaluate(scenario.Workspace, scenario.Goal));

        var output = await ReviseAsync(scenario, RevisedBrief, joined);

        Assert.Contains("refined-criteria=reset", output, StringComparison.Ordinal);
        var pending = await AssertPendingAsync(scenario);
        Assert.Equal(GoalRefinementReadiness.Pending, Readiness(scenario, pending));
        Assert.Null(SpecRefinementLaunchAttemptStore.ForWorkspace(scenario.Workspace).Get(scenario.GoalId));
    }

    private async Task<Scenario> CreateScenarioAsync(bool hasRow = true)
    {
        var workspace = CreateRefinedWorkspace(CreateTempDirectory());
        ModelFunctionCatalogStore.Save(workspace.ModelFunctionCatalogPath, new ModelFunctionCatalog([
            new ModelFunctionBinding(ModelFunctionPurposes.SpecRefiner, ModelLane.CheapApi,
                new ModelProfile(ProviderName, "fake-model", ModelCapability.Text, SubscriptionMode.ApiKey))
        ]));
        var repository = new SqliteOrchestratorStateRepository(workspace.SqliteStatePath);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal(OriginalBrief);
        GoalRefinementWorkCoordinator.RecordPending(kernel, goal.Id);
        await repository.SaveAsync(kernel);
        if (hasRow) await repository.EnsureOutboxMessageAsync(GoalRefinementWorkCoordinator.CreateMessage(goal.Id));
        return new(workspace, repository, kernel, goal.Id);
    }

    private async Task<string> ReviseAsync(Scenario scenario, string brief, bool joined)
    {
        string output;
        if (joined)
        {
            output = await scenario.Repository.TransactGoalCreationWithOutboxAsync(
                GoalCreationLoadScope.ForGoals([scenario.GoalId], []), (stored, _) =>
                {
                    var result = ExecuteCliAndCaptureResult(["revise", scenario.GoalId.Value, brief], stored, scenario.Workspace);
                    Assert.True(result.Changed);
                    return Task.FromResult((result.Changed, result.Output, (IReadOnlyList<OrchestratorStateOutboxMessage>)[]));
                });
        }
        else
        {
            var result = ExecuteCliAndCaptureResult(["revise", scenario.GoalId.Value, brief], scenario.Kernel, scenario.Workspace);
            Assert.True(result.Changed);
            output = result.Output;
            await scenario.Repository.SaveAsync(scenario.Kernel);
        }
        scenario.Kernel = await scenario.Repository.LoadAsync();
        return output;
    }

    private static Task<GoalRefinementWorkProcessResult> ProcessAsync(Scenario scenario, RecordingProvider provider) =>
        GoalRefinementWorkCoordinator.ProcessAsync(scenario.Repository, scenario.Workspace,
            new InMemoryModelProviderRegistry([provider]), WorkerProfileCatalog.Default(), scenario.GoalId);

    private static GoalRefinementReadiness Readiness(Scenario scenario, OrchestratorStateOutboxState state) =>
        GoalRefinementGate.Evaluate(scenario.Goal, state, new([])).Readiness;

    private static async Task<OrchestratorStateOutboxState> AssertPendingAsync(Scenario scenario, string?[]? before = null)
    {
        var state = await scenario.Repository.GetOutboxStateAsync(scenario.MessageId);
        Assert.NotNull(state);
        Assert.Equal(OrchestratorStateOutboxStatus.Pending, state.Status);
        Assert.Null(state.Detail);
        Assert.Null(state.ProcessingStartedAt);
        var after = ReadRow(scenario);
        Assert.NotNull(after);
        Assert.All(after.Skip(4), value => Assert.Null(value));
        if (before is not null) Assert.Equal(before.Take(4), after.Take(4));
        Assert.Single(await scenario.Repository.ListOutboxMessagesAsync(GoalRefinementWorkCoordinator.OutboxKind));
        return state;
    }

    private static void SeedAttemptState(Scenario scenario, DateTimeOffset createdAt)
    {
        var timestamp = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);
        SpecRefinementLaunchAttemptStore.ForWorkspace(scenario.Workspace).Save(scenario.GoalId,
            new(timestamp, 3, timestamp, scenario.MessageId, scenario.Workspace.SqliteStatePath));
        SpecRefinementTransientRetryStore.ForWorkspace(scenario.Workspace).Save(scenario.GoalId,
            new(scenario.MessageId, createdAt, 3, "SPEC_REFINEMENT_FAILED previous attempt", timestamp));
    }

    private static SqliteConnection OpenConnection(Scenario scenario)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = scenario.Workspace.SqliteStatePath, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }

    private static string?[]? ReadRow(Scenario scenario)
    {
        using var connection = OpenConnection(scenario);
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, kind, payload_json, created_at, quarantined_at, quarantine_reason,
                   processing_token, processing_started_at FROM state_outbox WHERE id = $id
            """;
        command.Parameters.AddWithValue("$id", scenario.MessageId);
        using var reader = command.ExecuteReader();
        if (!reader.Read()) return null;
        var row = Enumerable.Range(0, reader.FieldCount)
            .Select(index => reader.IsDBNull(index) ? null : reader.GetString(index)).ToArray();
        Assert.False(reader.Read());
        return row;
    }

    private static void SetClaim(Scenario scenario, string startedAt, bool quarantined = false)
    {
        using var connection = OpenConnection(scenario);
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE state_outbox SET processing_token = 'test-owned-lease', processing_started_at = $started,
                quarantined_at = $quarantined, quarantine_reason = $reason WHERE id = $id
            """;
        command.Parameters.AddWithValue("$started", startedAt);
        command.Parameters.AddWithValue("$quarantined", quarantined ? startedAt : DBNull.Value);
        command.Parameters.AddWithValue("$reason", quarantined ? "SPEC_REFINEMENT_FAILED expired attempt" : DBNull.Value);
        command.Parameters.AddWithValue("$id", scenario.MessageId);
        Assert.Equal(1, command.ExecuteNonQuery());
    }

    private sealed class Scenario(OrchestratorWorkspace workspace, SqliteOrchestratorStateRepository repository,
        AgentOrchestratorKernel kernel, GoalId goalId)
    {
        public OrchestratorWorkspace Workspace { get; } = workspace;
        public SqliteOrchestratorStateRepository Repository { get; } = repository;
        public AgentOrchestratorKernel Kernel { get; set; } = kernel;
        public GoalId GoalId { get; } = goalId;
        public Goal Goal => Kernel.GetGoal(GoalId);
        public string MessageId => GoalRefinementWorkCoordinator.MessageId(GoalId);
    }

    private sealed class RecordingProvider(string response) : IModelProvider
    {
        public string ProviderName => ReviseAfterRefinementFailureTests.ProviderName;
        public int Calls { get; private set; }
        public ModelRequest? Request { get; private set; }
        public Task<ModelResponse> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            return Task.FromResult(new ModelResponse(response, new ModelUsage(1, 1), "stop"));
        }
    }
}
