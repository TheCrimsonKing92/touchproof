using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: private SQLite databases, fixed injected times and no provider processes.
public sealed class ProviderConnectionEvidenceStoreTests
{
    [Theory]
    [InlineData("inline")]
    [InlineData("table")]
    [InlineData("archive")]
    public async Task LatestAttributableFailure_PreservesContextTimeAndHistoryAuthority(string representation)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = Seed();
        if (representation == "inline") fixture.InsertLegacy(goal);
        else
        {
            await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
            fixture.MarkStoredLatest(goal.Id);
            if (representation == "archive")
            {
                Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTaskHistoryTestFixture.At.AddDays(1)));
                using var stub = JsonDocument.Parse(fixture.RawGoal(goal.Id));
                Assert.True(stub.RootElement.GetProperty("IsMetadataOnly").GetBoolean());
                Assert.Equal([goal.Id], fixture.Rows("SELECT goal_id FROM goal_snapshot_archive"));
            }
        }
        var rowsBefore = fixture.HistoryRows();
        var observations = await SqliteOrchestratorStateRepository.OpenReadOnly(fixture.DatabasePath)
            .ReadProviderConnectionEvidenceAsync("grok-cli");
        var worker = Assert.Single(observations);
        Assert.Equal(ProviderConnectionResult.AuthenticationFailed, worker.Result);
        Assert.Equal(ProviderConnectionContext.WorkerSandbox, worker.ExecutionContext);
        Assert.Equal(GoalTaskHistoryTestFixture.At.AddMinutes(3), worker.ObservedAt);
        Assert.True(worker.IsHistorical);
        Assert.Equal(rowsBefore, fixture.HistoryRows());
    }

    [Fact]
    public async Task UnrelatedLatestDispatch_AndUnattributedVerificationNeverBecomeSuccess()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = Seed();
        goal = goal with { Tasks = goal.Tasks.Select(task => task with
        {
            LastVerification = task.LastVerification! with { DispatchAttemptAt = null },
            VerificationHistory = [], DispatchHistory = [],
            LastDispatch = task.LastDispatch! with { WorkerName = "grok-cli", DispatchedAt = GoalTaskHistoryTestFixture.At.AddHours(1) }
        }).ToArray() };
        fixture.InsertLegacy(goal);
        Assert.Empty(await fixture.Repository.ReadProviderConnectionEvidenceAsync("grok-cli"));
    }

    [Fact]
    public async Task CheckThenSeparateReadOnlyListing_RetainsHostAndNewerWorkerFailureWithoutProbing()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        await fixture.Repository.SaveGoalSnapshotsAsync([Seed("codex-cli")]);
        var at = GoalTaskHistoryTestFixture.At.AddDays(10);
        var check = new ProviderConnectionReadinessService(WorkerProfileCatalog.Default(), fixture.Repository,
            new("fixture", (_, _) => Task.FromResult(new WorkerProcessRunResult(0, "", "Logged in using ChatGPT")),
                _ => "codex.exe", new FixedTime(at)), new FixedTime(at));
        Assert.Equal(2, Assert.Single((await check.CheckAsync("codex-cli", fixture.Repository)).Profiles).Observations.Count);
        var before = fixture.RawGoal(Seed().Id);
        var service = new ProviderConnectionReadinessService(new([WorkerProfileCatalog.Default().GetRequired("codex-cli")]),
            SqliteOrchestratorStateRepository.OpenReadOnly(fixture.DatabasePath),
            new("fixture", (_, _) => throw new Exception("Listing must not probe"), _ => throw new Exception("Listing must not inspect tools")),
            new FixedTime(at.AddDays(1)));
        var report = Assert.Single((await service.ListAsync()).Profiles);
        Assert.Contains(report.Observations, o => o.Result == ProviderConnectionResult.SignedIn && o.ObservedAt == at && o.AgeSeconds == 86400);
        Assert.Contains(report.Observations, o => o.Result == ProviderConnectionResult.AuthenticationFailed && o.IsHistorical);
        Assert.Equal(before, fixture.RawGoal(Seed().Id));
        Assert.Equal(2, (await fixture.Repository.ReadProviderConnectionEvidenceAsync("codex-cli")).Count);
    }

    [Fact]
    public async Task MarkedHistoryWithMissingRows_FailsLoudly()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        await fixture.Repository.SaveGoalSnapshotsAsync([Seed()]);
        fixture.Execute("DELETE FROM goal_task_history WHERE kind = 'verification' AND sequence = 1");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.ReadProviderConnectionEvidenceAsync("grok-cli"));
    }

    [Fact]
    public async Task MissingOptionalObservationTable_RemainsReadableWithoutMigration()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        fixture.Execute("DROP TABLE provider_connection_observations");
        Assert.Empty(await SqliteOrchestratorStateRepository.OpenReadOnly(fixture.DatabasePath).ReadProviderConnectionEvidenceAsync("grok-cli"));
        Assert.Equal(["0"], fixture.Rows("SELECT COUNT(*) FROM sqlite_master WHERE name='provider_connection_observations'"));
    }

    [Fact]
    public async Task CatalogListing_ProjectsEachGoalOnceAndNeverHydratesKernelGraphs()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        await fixture.Repository.SaveGoalSnapshotsAsync([Seed()]);
        var reads = new List<GoalSnapshotReadObservation>();
        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.DatabasePath, reads.Add);
        var service = new ProviderConnectionReadinessService(WorkerProfileCatalog.Default(), repository,
            new("fixture", (_, _) => throw new Exception("must not probe")));
        var report = await service.ListAsync();
        Assert.Equal(WorkerProfileCatalog.Default().Profiles.Count, report.Profiles.Count);
        var projection = Assert.Single(reads);
        Assert.Equal("provider-connection-history-projection", projection.Operation);
    }

    private static GoalSnapshot Seed(string profile = "grok-cli")
    {
        var at = GoalTaskHistoryTestFixture.At;
        var seed = GoalTaskHistoryTestFixture.Snapshot(entries: 2, taskCount: 1);
        var task = seed.Tasks[0];
        var dispatches = task.DispatchHistory!.Select((d, i) => d with
        {
            WorkerName = profile, DispatchedAt = at.AddMinutes(i * 2), SandboxLowIntegrity = true
        }).ToArray();
        var verifications = task.VerificationHistory!.Select((v, i) => v with
        {
            CompletedAt = at.AddMinutes(i * 2 + 1), DispatchAttemptAt = dispatches[i].DispatchedAt,
            EvidenceSource = VerificationEvidenceSource.Dispatch, WorkerResultPresent = true,
            ExitCode = i, ProviderFailureKind = i == 1 ? ProviderFailureKind.AuthenticationRequired : ProviderFailureKind.Unknown
        }).ToArray();
        // A terminal goal with no build custody has an empty collection, as domain exports do.
        // JSON null is a row to json_each and would make the archive's open-work guard hold it.
        return seed with { Tasks = [task with { WorkerBuildInvocations = [], LastDispatch = dispatches[^1], DispatchHistory = dispatches,
            LastVerification = verifications[^1], VerificationHistory = verifications }] };
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
