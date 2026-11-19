using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each case owns its database and journal root; no process or environment mutation.
public sealed class ConductLoopKernelDependencyFallbackTests
{
    [Xunit.Theory]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, true)]
    public async Task LoadKernel_QueriesDependencyStatusAndPreservesLanding(bool landed, bool tracked)
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var db = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var inner = new SqliteOrchestratorStateRepository(db);
            var kernel = new AgentOrchestratorKernel();
            var dependency = kernel.CreateGoal("Completed dependency", [
                new TaskSpec(TaskId.New(), "Done", AgentRole.Developer)
            ]);
            var dependent = kernel.CreateGoal("Hydrated dependent", [
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)
            ]);
            kernel.ActivateGoal(dependency.Id, AgentCatalog.Default().Agents);
            kernel.ActivateGoal(dependent.Id, AgentCatalog.Default().Agents);
            kernel.SetGoalDependency(dependent.Id, dependency.Id);
            var snapshot = kernel.ExportSnapshot();
            kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = snapshot.Goals.Select(goal => goal.Id == dependency.Id.Value
                    ? goal with { Status = GoalStatus.Completed }
                    : goal).ToArray()
            });
            await inner.SaveAsync(kernel);
            if (landed)
            {
                GoalOperationJournal.RecordLandingIntent(root, dependency,
                    $"goal/{dependency.Id.Value[..8]}", "main", "abcdef1234567890", "test:landing");
                GoalOperationJournal.Completed(root, dependency, "conductor:land", "Verified fixture landing.");
            }

            var repository = new RecordingRepository(inner);
            string[] requestedIds = tracked ? [dependency.Id.Value] : [];
            var loaded = CliPersistentStateRunner.LoadConductLoopKernel(repository, requestedIds, root);

            Assert.Equal(tracked ? 2 : 1, loaded.Goals.Count);
            var hydrated = Assert.Single(loaded.Goals, goal => goal.Id == dependent.Id);
            Assert.Contains(dependency.Id, hydrated.DependsOn);
            Assert.Equal(tracked, loaded.Goals.Any(goal => goal.Id == dependency.Id));
            Assert.Equal(0, repository.ConductMetadataCalls);
            Assert.Equal(0, repository.FullMetadataCalls);
            Assert.Equal(requestedIds, Assert.Single(repository.LiveMetadataRequests));
            Assert.Equal(dependency.Id.Value, Assert.Single(Assert.Single(repository.StatusRequests)));
            Assert.True(loaded.TryGetKnownDependencyGoalStatus(dependency.Id, out var status));
            Assert.Equal(GoalStatus.Completed.ToString(), status);
            Assert.Equal(landed, loaded.IsKnownCompletedDependencyGoal(dependency.Id));
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Xunit.Fact]
    public async Task RepositoryFiltersMetadataAndProjectsOnlyRequestedStatuses()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var db = Path.Combine(root, "state.db");
            StateDbMigrations.EnsureUpToDate(db);
            var statements = new List<string>();
            var repository = new SqliteOrchestratorStateRepository(db, statements.Add);
            var kernel = new AgentOrchestratorKernel();
            var active = kernel.CreateGoal("Active goal", [new TaskSpec(TaskId.New(), "Test", AgentRole.Planner)]);
            var parked = kernel.CreateGoal("Parked goal", [new TaskSpec(TaskId.New(), "Test", AgentRole.Planner)]);
            var requested = kernel.CreateGoal("Requested terminal", [new TaskSpec(TaskId.New(), "Test", AgentRole.Planner)]);
            var unrelated = kernel.CreateGoal("Unrelated terminal", [new TaskSpec(TaskId.New(), "Test", AgentRole.Planner)]);
            var snapshots = kernel.ExportSnapshot().Goals.ToDictionary(goal => goal.Id, StringComparer.Ordinal);
            await repository.SaveGoalSnapshotsAsync([
                snapshots[active.Id.Value] with { Status = GoalStatus.Active },
                snapshots[parked.Id.Value] with { Status = GoalStatus.Parked },
                snapshots[requested.Id.Value] with { Status = GoalStatus.Completed },
                snapshots[unrelated.Id.Value] with { Status = GoalStatus.Failed }
            ]);
            statements.Clear();

            var largeIdSet = Enumerable.Range(0, 2000).Select(_ => GoalId.New().Value)
                .Append(requested.Id.Value).ToArray();
            var metadata = await repository.ListLiveConductLoopGoalMetadataAsync(largeIdSet);
            Assert.Equal(new[] { active.Id.Value, parked.Id.Value, requested.Id.Value }.Order(StringComparer.Ordinal),
                metadata.Select(row => row.Id).Order(StringComparer.Ordinal));
            Assert.Equal(metadata.Select(row => row.UpdatedAt).OrderDescending(StringComparer.Ordinal),
                metadata.Select(row => row.UpdatedAt));
            var listSql = Assert.Single(statements);
            Assert.Contains("WHERE status COLLATE NOCASE NOT IN", listSql, StringComparison.Ordinal);
            Assert.Contains("OR id IN (SELECT value FROM json_each($ids))", listSql, StringComparison.Ordinal);
            Assert.Contains("ORDER BY updated_at DESC", listSql, StringComparison.Ordinal);

            var liveOnly = await repository.ListLiveConductLoopGoalMetadataAsync([]);
            Assert.Equal(new[] { active.Id.Value, parked.Id.Value }.Order(StringComparer.Ordinal),
                liveOnly.Select(row => row.Id).Order(StringComparer.Ordinal));
            statements.Clear();

            Assert.Empty(await repository.ListConductLoopGoalStatusesByIdAsync([]));
            Assert.Empty(statements);
            var status = Assert.Single(await repository.ListConductLoopGoalStatusesByIdAsync(largeIdSet));
            Assert.Equal(requested.Id.Value, status.Key);
            Assert.Equal(nameof(GoalStatus.Completed), status.Value);
            var statusSql = Assert.Single(statements);
            Assert.StartsWith("SELECT id, status", statusSql, StringComparison.Ordinal);
            Assert.DoesNotContain("snapshot_json", statusSql, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    private sealed class RecordingRepository(SqliteOrchestratorStateRepository inner)
        : ITransactionalOrchestratorStateRepository
    {
        public int FullMetadataCalls { get; private set; }
        public int ConductMetadataCalls { get; private set; }
        public List<string[]> LiveMetadataRequests { get; } = [];
        public List<string[]> StatusRequests { get; } = [];

        public Task<IReadOnlyList<GoalSummary>> ListGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            FullMetadataCalls++;
            return inner.ListGoalMetadataAsync(cancellationToken);
        }

        public Task<IReadOnlyList<GoalSummary>> ListConductLoopGoalMetadataAsync(CancellationToken cancellationToken = default)
        {
            ConductMetadataCalls++;
            return inner.ListConductLoopGoalMetadataAsync(cancellationToken);
        }

        public Task<IReadOnlyList<GoalSummary>> ListLiveConductLoopGoalMetadataAsync(
            IReadOnlyCollection<string> requestedGoalIds, CancellationToken cancellationToken = default)
        {
            LiveMetadataRequests.Add(requestedGoalIds.ToArray());
            return inner.ListLiveConductLoopGoalMetadataAsync(requestedGoalIds, cancellationToken);
        }

        public Task<IReadOnlyDictionary<string, string>> ListConductLoopGoalStatusesByIdAsync(
            IReadOnlyCollection<string> goalIds, CancellationToken cancellationToken = default)
        {
            StatusRequests.Add(goalIds.ToArray());
            return inner.ListConductLoopGoalStatusesByIdAsync(goalIds, cancellationToken);
        }

        // Inherit ListGoalIdStatusesAsync: switching the fallback to another query must also be counted.
        public Task<AgentOrchestratorKernel> LoadAsync(CancellationToken cancellationToken = default) =>
            inner.LoadAsync(cancellationToken);

        public Task SaveAsync(AgentOrchestratorKernel kernel, CancellationToken cancellationToken = default) =>
            inner.SaveAsync(kernel, cancellationToken);

        public Task<AgentOrchestratorKernel> LoadGoalsAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            inner.LoadGoalsAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<GoalId>> ListGoalIdsWithCompletedHumanInputAsync(
            IReadOnlyCollection<GoalId> goalIds, CancellationToken cancellationToken = default) =>
            inner.ListGoalIdsWithCompletedHumanInputAsync(goalIds, cancellationToken);

        public Task<IReadOnlyList<ModelFitHistoryRow>> ListModelFitHistoryAsync(CancellationToken cancellationToken = default) =>
            inner.ListModelFitHistoryAsync(cancellationToken);

        public Task<IReadOnlyList<ModelOutcomeRecord>> BuildModelOutcomeScorecardAsync(
            int windowSize = ModelOutcomeScorecard.DefaultWindowSize, CancellationToken cancellationToken = default) =>
            inner.BuildModelOutcomeScorecardAsync(windowSize, cancellationToken);

        public Task<ModelFitBestFit?> QueryBestFitForRoleAsync(
            AgentRole role, CancellationToken cancellationToken = default) =>
            inner.QueryBestFitForRoleAsync(role, cancellationToken);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactAsync(transaction, cancellationToken);

        public Task<T> TransactAsync<T>(
            Func<AgentOrchestratorKernel, Func<Task>, CancellationToken, Task<(bool ShouldSave, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactAsync(transaction, cancellationToken);

        public Task<GoalSnapshot?> LoadGoalAsync(GoalId goalId, CancellationToken cancellationToken = default) =>
            inner.LoadGoalAsync(goalId, cancellationToken);

        public Task SaveGoalSnapshotsAsync(
            IReadOnlyCollection<GoalSnapshot> goals, CancellationToken cancellationToken = default) =>
            inner.SaveGoalSnapshotsAsync(goals, cancellationToken);

        public Task<T> TransactGoalAsync<T>(
            GoalId goalId,
            Func<GoalSnapshot?, CancellationToken, Task<(bool ShouldSave, GoalSnapshot? NewSnapshot, T Result)>> transaction,
            CancellationToken cancellationToken = default) =>
            inner.TransactGoalAsync(goalId, transaction, cancellationToken);
    }
}
