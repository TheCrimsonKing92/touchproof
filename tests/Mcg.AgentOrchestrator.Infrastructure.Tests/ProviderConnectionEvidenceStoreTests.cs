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

    [Theory]
    [InlineData("inline")]
    [InlineData("table")]
    [InlineData("archive")]
    public async Task RecordedOrchestratorFailure_WithZeroExitRemainsFailed(string representation)
    {
        const string diagnostic = "private-orchestrator-failure-detail";
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = Seed();
        var task = Assert.Single(goal.Tasks);
        var failed = task.LastVerification! with
        {
            ExitCode = 0, ProviderFailureKind = ProviderFailureKind.Unknown, OrchestratorFailureReason = diagnostic
        };
        goal = goal with { Tasks = [task with { LastVerification = failed, VerificationHistory = [task.VerificationHistory![0], failed] }] };
        if (representation == "inline") fixture.InsertLegacy(goal);
        else
        {
            await fixture.Repository.SaveGoalSnapshotsAsync([goal]);
            if (representation == "table") fixture.MarkStoredLatest(goal.Id);
            else
            {
                fixture.InlineStoredLatest(goal);
                Assert.Equal(1, await fixture.Repository.ArchiveTerminalGoalSnapshotsAsync(GoalTaskHistoryTestFixture.At.AddDays(1)));
                using var stub = JsonDocument.Parse(fixture.RawGoal(goal.Id));
                Assert.True(stub.RootElement.GetProperty("IsMetadataOnly").GetBoolean());
            }
        }

        var worker = Assert.Single(await fixture.Repository.ReadProviderConnectionEvidenceAsync("grok-cli"));
        Assert.Equal(ProviderConnectionResult.WorkerFailed, worker.Result);
        Assert.Equal(failed.CompletedAt, worker.ObservedAt);
        Assert.DoesNotContain(diagnostic, JsonSerializer.Serialize(worker));
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

    [Theory]
    [InlineData("inline")]
    [InlineData("table")]
    [InlineData("archive")]
    public async Task DeveloperOutcomeWithoutAttemptId_UsesRecordedProcessStart(string representation)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = Seed();
        var task = Assert.Single(goal.Tasks);
        var failed = task.LastVerification! with
        {
            DispatchAttemptAt = null,
            DispatchStartedAt = task.LastDispatch!.DispatchedAt.AddSeconds(10)
        };
        var unrelated = task.LastDispatch with
        {
            WorkerName = "codex-cli", DispatchedAt = failed.CompletedAt.AddSeconds(-10)
        };
        goal = goal with { Tasks = [task with
        {
            LastVerification = failed, VerificationHistory = [task.VerificationHistory![0], failed],
            LastDispatch = unrelated, DispatchHistory = [.. task.DispatchHistory!, unrelated]
        }] };
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
            }
        }

        var observation = Assert.Single(await fixture.Repository.ReadProviderConnectionEvidenceAsync("grok-cli"));
        Assert.Equal(ProviderConnectionResult.AuthenticationFailed, observation.Result);
        Assert.Equal(ProviderConnectionContext.WorkerSandbox, observation.ExecutionContext);
        Assert.Equal(failed.CompletedAt, observation.ObservedAt);
        Assert.True(observation.IsHistorical);
        Assert.Empty(await fixture.Repository.ReadProviderConnectionEvidenceAsync("codex-cli"));
    }

    [Fact]
    public async Task DeveloperOutcomeWithConflictingDispatchesAtStart_RemainsUnattributed()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var goal = Seed();
        var task = Assert.Single(goal.Tasks);
        var dispatch = task.LastDispatch!;
        var failed = task.LastVerification! with
        {
            DispatchAttemptAt = null, DispatchStartedAt = dispatch.DispatchedAt.AddSeconds(10)
        };
        fixture.InsertLegacy(goal with { Tasks = [task with
        {
            LastVerification = failed, VerificationHistory = [failed],
            DispatchHistory = [dispatch, dispatch with { WorkerName = "codex-cli" }]
        }] });
        Assert.Empty(await fixture.Repository.ReadProviderConnectionEvidenceAsync("grok-cli"));
        Assert.Empty(await fixture.Repository.ReadProviderConnectionEvidenceAsync("codex-cli"));
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
    public async Task Check_ReturnsItsOwnSamplesWhenListingHasNewerObservations()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        const string profile = "claude-cli";
        await fixture.Repository.SaveGoalSnapshotsAsync([Seed(profile)]);
        var at = GoalTaskHistoryTestFixture.At.AddDays(10);
        var newerAt = at.AddMinutes(1);
        var catalog = new WorkerProfileCatalog([WorkerProfileCatalog.Default().GetRequired(profile)]);
        var newer = new ProviderConnectionReadinessService(catalog, fixture.Repository,
            new("fixture", (_, _) => Task.FromResult(new WorkerProcessRunResult(0, "{\"loggedIn\":true}", "")),
                _ => "claude.exe", new FixedTime(newerAt)), new FixedTime(newerAt),
            claudeProbe: () => new(true, false, null, null, false));
        await newer.CheckAsync(profile, fixture.Repository);

        // A delayed write of an older sample must leave the newer listing rows intact.
        var delayed = new ProviderConnectionReadinessService(catalog, fixture.Repository,
            new("fixture", (_, _) => Task.FromResult(new WorkerProcessRunResult(1, "{\"loggedIn\":false}", "")),
                _ => "claude.exe", new FixedTime(at)), new FixedTime(at),
            claudeProbe: () => new(false, false, null, null, false));
        var check = Assert.Single((await delayed.CheckAsync(profile, fixture.Repository)).Profiles);
        Assert.Collection(check.Observations,
            host => Assert.Equal((ProviderConnectionResult.SignedOut, (DateTimeOffset?)at), (host.Result, host.ObservedAt)),
            material => Assert.Equal((ProviderConnectionResult.MaterialUnavailable, (DateTimeOffset?)at), (material.Result, material.ObservedAt)),
            worker => Assert.Equal(ProviderConnectionResult.AuthenticationFailed, worker.Result));

        var listing = Assert.Single((await newer.ListAsync()).Profiles);
        Assert.Collection(listing.Observations,
            host => Assert.Equal((ProviderConnectionResult.SignedIn, (DateTimeOffset?)newerAt), (host.Result, host.ObservedAt)),
            material => Assert.Equal((ProviderConnectionResult.MaterialPresent, (DateTimeOffset?)newerAt), (material.Result, material.ObservedAt)),
            worker => Assert.Equal(ProviderConnectionResult.AuthenticationFailed, worker.Result));
    }

    [Fact]
    public async Task MarkedHistoryWithMissingRows_FailsLoudly()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        await fixture.Repository.SaveGoalSnapshotsAsync([Seed()]);
        fixture.Execute("DELETE FROM goal_task_history WHERE kind = 'verification' AND sequence = 1");
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.Repository.ReadProviderConnectionEvidenceAsync("grok-cli"));
    }

    [Theory]
    [InlineData(false, "codex-cli", "CODEX-CLI")]
    [InlineData(true, "codex-cli", "CODEX-CLI")]
    [InlineData(false, "café-worker", "CAFÉ-WORKER")]
    [InlineData(true, "café-worker", "CAFÉ-WORKER")]
    [InlineData(false, "\U00010428-worker", "\U00010400-WORKER")]
    [InlineData(true, "\U00010428-worker", "\U00010400-WORKER")]
    public async Task CaseOnlyCatalogRename_RetainsLatestHostObservation(
        bool newerFirst, string lowerProfile, string upperProfile)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var at = GoalTaskHistoryTestFixture.At;
        var older = new ProviderConnectionObservation(ProviderConnectionContext.Host,
            ProviderConnectionSource.CodexLoginStatus, ProviderConnectionResult.SignedIn,
            ProviderConnectionAuthMode.OAuth, at);
        var newer = older with { Result = ProviderConnectionResult.SignedOut, ObservedAt = at.AddMinutes(1) };
        if (newerFirst)
        {
            await fixture.Repository.SaveProviderConnectionObservationAsync(upperProfile, newer);
            await fixture.Repository.SaveProviderConnectionObservationAsync(lowerProfile, older);
        }
        else
        {
            await fixture.Repository.SaveProviderConnectionObservationAsync(lowerProfile, older);
            await fixture.Repository.SaveProviderConnectionObservationAsync(upperProfile, newer);
        }
        var profile = WorkerProfileCatalog.Default().GetRequired("codex-cli") with { Name = upperProfile };
        Assert.Equal(profile, new WorkerProfileCatalog([profile]).GetRequired(lowerProfile));
        var service = new ProviderConnectionReadinessService(new([profile]),
            SqliteOrchestratorStateRepository.OpenReadOnly(fixture.DatabasePath),
            new("fixture", (_, _) => throw new Exception("Listing must not probe")), new FixedTime(at.AddMinutes(2)));

        var report = Assert.Single((await service.ListAsync()).Profiles);
        var observation = Assert.Single(report.Observations.Where(o => o.ExecutionContext == ProviderConnectionContext.Host));
        Assert.Equal(ProviderConnectionResult.SignedOut, observation.Result);
        Assert.Equal(newer.ObservedAt, observation.ObservedAt);
        Assert.Equal(newer, Assert.Single(await fixture.Repository.ReadProviderConnectionEvidenceAsync(lowerProfile)));
        Assert.Equal(["1"], fixture.Rows("SELECT COUNT(*) FROM provider_connection_observations"));
    }

    [Theory]
    [InlineData(false, "claude-cli", "CLAUDE-CLI")]
    [InlineData(true, "claude-cli", "CLAUDE-CLI")]
    [InlineData(false, "café-worker", "CAFÉ-WORKER")]
    [InlineData(true, "café-worker", "CAFÉ-WORKER")]
    public async Task UpgradeFromStep15_RecordsStep16AndKeepsLatestObservationPerContext(
        bool newerFirst, string lowerProfile, string upperProfile)
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        fixture.Execute("""
            DROP TABLE provider_connection_observations;
            DELETE FROM schema_migrations WHERE migration_number > 15;
            """);
        Assert.Equal(15, StateDbMigrations.Plan(fixture.DatabasePath).RecordedVersion);
        StateDbMigrations.EnsureUpToDate(fixture.DatabasePath);
        Assert.True(StateDbMigrations.IsUpToDate(fixture.DatabasePath));
        Assert.Equal(["16:provider-connection-observations"], fixture.Rows("""
            SELECT migration_number || ':' || name FROM schema_migrations
            WHERE migration_number = 16 OR name LIKE 'provider-connection-%'
            ORDER BY migration_number
            """));

        var at = GoalTaskHistoryTestFixture.At;
        var older = new ProviderConnectionObservation(ProviderConnectionContext.Host,
            ProviderConnectionSource.ClaudeAuthStatus, ProviderConnectionResult.SignedIn,
            ProviderConnectionAuthMode.OAuth, at);
        var newer = older with { Result = ProviderConnectionResult.SignedOut, ObservedAt = at.AddMinutes(1) };
        var material = new ProviderConnectionObservation(ProviderConnectionContext.WorkerCredentialMaterial,
            ProviderConnectionSource.ClaudePreflight, ProviderConnectionResult.MaterialPresent,
            ProviderConnectionAuthMode.OAuth, at.AddMinutes(-1));
        var entries = newerFirst
            ? new[] { (upperProfile, newer), (lowerProfile, older), (lowerProfile, material) }
            : new[] { (lowerProfile, older), (upperProfile, newer), (lowerProfile, material) };
        foreach (var (profile, observation) in entries)
            await fixture.Repository.SaveProviderConnectionObservationAsync(profile, observation);

        var readOnly = SqliteOrchestratorStateRepository.OpenReadOnly(fixture.DatabasePath);
        var observations = await readOnly.ReadProviderConnectionEvidenceAsync(upperProfile);
        Assert.Equal(2, observations.Count);
        Assert.Contains(newer, observations);
        Assert.Contains(material, observations);
        Assert.Equal(observations, await readOnly.ReadProviderConnectionEvidenceAsync(lowerProfile));
        Assert.Equal(["2"], fixture.Rows("SELECT COUNT(*) FROM provider_connection_observations"));
    }

    [Fact]
    public async Task DistinctUnicodeProfiles_KeepIndependentObservations()
    {
        using var fixture = new GoalTaskHistoryTestFixture();
        var at = GoalTaskHistoryTestFixture.At;
        var signedIn = new ProviderConnectionObservation(ProviderConnectionContext.Host,
            ProviderConnectionSource.CodexLoginStatus, ProviderConnectionResult.SignedIn,
            ProviderConnectionAuthMode.OAuth, at);
        var signedOut = signedIn with { Result = ProviderConnectionResult.SignedOut };
        await fixture.Repository.SaveProviderConnectionObservationAsync("café-worker", signedIn);
        await fixture.Repository.SaveProviderConnectionObservationAsync("cafe-worker", signedOut);

        var observations = await fixture.Repository.ReadProviderConnectionEvidenceAsync(["CAFÉ-WORKER", "CAFE-WORKER"]);
        Assert.Equal(signedIn, Assert.Single(observations["CAFÉ-WORKER"]));
        Assert.Equal(signedOut, Assert.Single(observations["CAFE-WORKER"]));
        Assert.Equal(["2"], fixture.Rows("SELECT COUNT(*) FROM provider_connection_observations"));
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
