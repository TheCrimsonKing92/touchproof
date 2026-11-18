using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its workspace, SQLite stores and journal; time and drains are injected.
public sealed class MaintenanceStepReceiptsTests
{
    private static readonly DateTimeOffset Now = new(2020, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly RunEventMaintenanceResult CompletedMaintenance = new(
        Deferred: false, DeferredReason: null, ConductorTickRowsDeleted: 0,
        AgedConductorTickRowsDeleted: 0, OversizedConductorTickRowsDeleted: 0,
        DeletedPayloadBytesEstimate: 0, MaxRowsDeletedInTransaction: 0, Duration: TimeSpan.Zero,
        BytesBefore: 0, BytesAfter: 0, VacuumRequested: false, VacuumCompleted: false, VacuumDeferred: false);
    private static readonly StorageRetentionResult SuccessfulSweep = new(0, 0, 0, 0, 0, 0, "fixture-sweep", []);

    [Xunit.Fact]
    public void FreshRetention_MissingSteps_RunOnceAcrossDailyWindow()
    {
        using var fixture = new Fixture();
        fixture.SeedFreshMaintenanceAndRetention(Now.AddHours(-1));
        var retentionCalls = 0;
        var archiveCalls = 0;
        var compressionCalls = 0;
        StorageRetentionResult Retention() { retentionCalls++; return SuccessfulSweep; }
        int Archive(DateTimeOffset _) { archiveCalls++; return 0; }
        GoalSnapshotArchiveRecompressionResult Compress(long _) { compressionCalls++; return new(0, 0, 0); }

        var first = fixture.Run(Now, Archive, Compress, retention: Retention);

        Assert.True(first.Attempted);
        Assert.False(first.Failed);
        Assert.Equal(0, retentionCalls);
        Assert.Equal(1, archiveCalls);
        Assert.Equal(1, compressionCalls);
        var archive = fixture.Read(MaintenanceStepReceipts.GoalSnapshotArchiveOperation);
        var compression = fixture.Read(MaintenanceStepReceipts.ArchivedSnapshotCompressionOperation);
        AssertCompletedAt(archive, Now);
        AssertCompletedAt(compression, Now);

        // Retention is due here, so this second call passes the in-memory gate and rereads step receipts.
        Assert.True(fixture.Run(Now.AddHours(23), Archive, Compress, retention: Retention).Attempted);
        Assert.Equal(1, retentionCalls);
        Assert.Equal(1, archiveCalls);
        Assert.Equal(1, compressionCalls);
        Assert.Equal(archive, fixture.Read(MaintenanceStepReceipts.GoalSnapshotArchiveOperation));
        Assert.Equal(compression, fixture.Read(MaintenanceStepReceipts.ArchivedSnapshotCompressionOperation));
        // Fresh steps are due an hour before the maintenance pass that just ran.
        Assert.True(RunEventMaintenanceCadence.IsKnownFresh(fixture.Workspace.RunEventStorePath,
            Now.AddHours(24).AddTicks(-1)));
        Assert.False(RunEventMaintenanceCadence.IsKnownFresh(fixture.Workspace.RunEventStorePath, Now.AddHours(24)));
    }

    [Xunit.Fact]
    public void CompressionFails_Continuation_RetriesOnlyCompression()
    {
        using var fixture = new Fixture();
        var archiveCalls = 0;
        var compressionCalls = 0;
        int Archive(DateTimeOffset _) { archiveCalls++; return 0; }
        GoalSnapshotArchiveRecompressionResult Compress(long _)
        {
            if (++compressionCalls == 1) throw new InvalidOperationException("fixture compression failure");
            return new(0, 0, 0);
        }
        var continuation = CompletedMaintenance with
        {
            Disposition = SqliteMaintenanceDisposition.Incomplete,
            NextAttemptAt = Now.AddMinutes(5)
        };

        var first = fixture.Run(Now, Archive, Compress, maintenance: continuation);

        Assert.True(first.Attempted);
        Assert.False(first.Failed);
        var archive = fixture.Read(MaintenanceStepReceipts.GoalSnapshotArchiveOperation);
        AssertCompletedAt(archive, Now);
        var failed = Assert.IsType<RunEventRecord>(fixture.Read(MaintenanceStepReceipts.ArchivedSnapshotCompressionOperation));
        Assert.Equal("Failed", failed.Status);
        Assert.Equal(Now, failed.OccurredAt);
        Assert.True(MaintenanceStepReceipts.IsDue(
            MaintenanceStepReceipts.ReadLatest(fixture.Store, MaintenanceStepReceipts.ArchivedSnapshotCompressionOperation), Now));
        Assert.Contains("ARCHIVED_SNAPSHOT_COMPRESSION status=failed", File.ReadAllText(fixture.Workspace.ConductEventsLogPath));
        Assert.Contains("exception=InvalidOperationException", failed.Detail);

        var second = fixture.Run(Now.AddMinutes(5), Archive, Compress);

        Assert.True(second.Attempted);
        Assert.False(second.Failed);
        Assert.Equal(1, archiveCalls);
        Assert.Equal(2, compressionCalls);
        Assert.Equal(archive, fixture.Read(MaintenanceStepReceipts.GoalSnapshotArchiveOperation));
        var completed = fixture.Read(MaintenanceStepReceipts.ArchivedSnapshotCompressionOperation);
        AssertCompletedAt(completed, Now.AddMinutes(5));
        Assert.True(completed!.Sequence > failed.Sequence);
    }

    [Xunit.Theory]
    [Xunit.InlineData("Completed")]
    [Xunit.InlineData("Incomplete")]
    public void FreshMaintenance_DueStep_PreventsEarlySkip(string disposition)
    {
        using var fixture = new Fixture();
        fixture.SeedFreshMaintenanceAndRetention(Now, disposition);
        MaintenanceStepReceipts.AppendArchive(fixture.Store,
            new(Now.AddDays(-2), 0, "completed", null), Now.AddHours(-23));
        var archiveCalls = 0;
        var compressionCalls = 0;

        Assert.True(fixture.Run(Now,
            _ => { archiveCalls++; return 0; },
            _ => { compressionCalls++; return new(0, 0, 0); }).Attempted);

        Assert.Equal(0, archiveCalls);
        Assert.Equal(1, compressionCalls);
        AssertCompletedAt(fixture.Read(MaintenanceStepReceipts.ArchivedSnapshotCompressionOperation), Now);
        // The archive deadline must shorten the completed pass's next-due cache.
        Assert.True(fixture.Run(Now.AddMinutes(30), _ => throw new InvalidOperationException("fresh archive ran"),
            _ => throw new InvalidOperationException("fresh compression ran")).Skipped);
        Assert.False(RunEventMaintenanceCadence.IsKnownFresh(fixture.Workspace.RunEventStorePath, Now.AddHours(1)));
    }

    [Xunit.Theory]
    [Xunit.InlineData("Completed")]
    [Xunit.InlineData("Incomplete")]
    public void FreshSteps_EarlySkip_CachesEarliestStepDeadline(string disposition)
    {
        using var fixture = new Fixture();
        fixture.SeedFreshMaintenanceAndRetention(Now, disposition);
        MaintenanceStepReceipts.AppendArchive(fixture.Store,
            new(Now.AddDays(-2), 0, "completed", null), Now.AddHours(-23));
        MaintenanceStepReceipts.AppendCompression(fixture.Store, new(0, 0, 0, 1, "completed", null), Now);

        Assert.True(fixture.Run(Now,
            _ => throw new InvalidOperationException("fresh archive ran"),
            _ => throw new InvalidOperationException("fresh compression ran")).Skipped);

        Assert.True(RunEventMaintenanceCadence.IsKnownFresh(fixture.Workspace.RunEventStorePath,
            Now.AddHours(1).AddTicks(-1)));
        Assert.False(RunEventMaintenanceCadence.IsKnownFresh(fixture.Workspace.RunEventStorePath, Now.AddHours(1)));
    }

    [Xunit.Fact]
    public void CompletedDrain_ReceiptUsesCompletionClock()
    {
        using var fixture = new Fixture();
        var clock = Now;
        var result = RunEventMaintenanceCadence.TryRunIfDue(
            fixture.Workspace.RunEventStorePath, fixture.Workspace.ConductEventsLogPath, () => clock,
            maintenanceOperation: (_, _) => CompletedMaintenance,
            workspace: fixture.Workspace,
            artifactRetentionOperation: (_, _, _, _) => SuccessfulSweep,
            goalSnapshotArchiveOperation: _ => { clock = Now.AddMinutes(2); return 0; },
            archivedSnapshotCompressionOperation: _ => { clock = Now.AddMinutes(3); return new(0, 0, 0); },
            archivedSnapshotCompressionEnabled: true);

        Assert.False(result.Failed);
        AssertCompletedAt(fixture.Read(MaintenanceStepReceipts.GoalSnapshotArchiveOperation), Now.AddMinutes(2));
        AssertCompletedAt(fixture.Read(MaintenanceStepReceipts.ArchivedSnapshotCompressionOperation), Now.AddMinutes(3));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void DrainCancels_PropagatesAndLeavesUnfinishedStepDue(bool compressionCancels)
    {
        using var fixture = new Fixture();
        var cancellation = new OperationCanceledException("fixture cancellation");

        Assert.Same(cancellation, Assert.Throws<OperationCanceledException>(() => fixture.Run(Now,
            _ => compressionCancels ? 0 : throw cancellation,
            _ => throw cancellation)));

        var archive = fixture.Read(MaintenanceStepReceipts.GoalSnapshotArchiveOperation);
        if (compressionCancels) AssertCompletedAt(archive, Now);
        else Assert.Null(archive);
        Assert.Null(fixture.Read(MaintenanceStepReceipts.ArchivedSnapshotCompressionOperation));
    }

    [Xunit.Fact]
    public void LatestFailedReceipt_OverridesOlderSuccessIndependently()
    {
        using var fixture = new Fixture();
        MaintenanceStepReceipts.AppendArchive(fixture.Store, new(Now.AddDays(-2), 0, "completed", null), Now);
        MaintenanceStepReceipts.AppendCompression(fixture.Store, new(0, 0, 0, 1, "completed", null), Now);
        var archive = fixture.Read(MaintenanceStepReceipts.GoalSnapshotArchiveOperation);
        MaintenanceStepReceipts.AppendCompression(fixture.Store,
            new(0, 0, 0, 1, "failed", "InvalidOperationException"), Now.AddMinutes(1));

        var latest = MaintenanceStepReceipts.ReadLatest(fixture.Store, MaintenanceStepReceipts.ArchivedSnapshotCompressionOperation);
        Assert.True(MaintenanceStepReceipts.IsDue(latest, Now.AddMinutes(1)));
        Assert.Null(MaintenanceStepReceipts.NextDue(latest));
        Assert.Equal(archive, fixture.Read(MaintenanceStepReceipts.GoalSnapshotArchiveOperation));
        Assert.False(MaintenanceStepReceipts.IsDue(
            MaintenanceStepReceipts.ReadLatest(fixture.Store, MaintenanceStepReceipts.GoalSnapshotArchiveOperation), Now.AddMinutes(1)));
    }

    [Xunit.Fact]
    public void DueDecision_UsesMissingFailureAndExactIntervalBoundary()
    {
        Assert.True(MaintenanceStepReceipts.IsDue(null, Now));
        Assert.Null(MaintenanceStepReceipts.NextDue(null));
        Assert.True(MaintenanceStepReceipts.IsDue(new(Now, true), Now));
        var success = new MaintenanceStepReceipt(Now, false);
        var deadline = Now.Add(RunEventMaintenanceCadence.Interval);
        Assert.Equal(deadline, MaintenanceStepReceipts.NextDue(success));
        Assert.False(MaintenanceStepReceipts.IsDue(success, deadline.AddTicks(-1)));
        Assert.True(MaintenanceStepReceipts.IsDue(success, deadline));
    }

    private static void AssertCompletedAt(RunEventRecord? receipt, DateTimeOffset at)
    {
        Assert.NotNull(receipt);
        Assert.Null(receipt.GoalId);
        Assert.Equal("Completed", receipt.Status);
        Assert.Equal(at, receipt.OccurredAt);
        Assert.False(string.IsNullOrWhiteSpace(receipt.PayloadJson));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), $"mcg-maintenance-steps-{Guid.NewGuid():N}");
        internal OrchestratorWorkspace Workspace { get; }
        internal SqliteRunEventStore Store { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(_root);
            Workspace = OrchestratorWorkspace.ForDirectory(_root);
            InfrastructureTestSupport.CreateMigratedStateRepository(Workspace.SqliteStatePath);
            Store = new SqliteRunEventStore(Workspace.RunEventStorePath);
        }

        internal void SeedFreshMaintenanceAndRetention(DateTimeOffset at, string disposition = "Completed")
        {
            RunEventMaintenanceCadence.AppendArtifactRetentionReceipt(Store, SuccessfulSweep, at);
            Store.AppendAsync(new RunEventAppend(RunEventTypes.RunEventMaintenance,
                GoalId: null, Operation: RunEventMaintenanceCadence.Operation, Status: "Completed", Detail: null,
                PayloadJson: System.Text.Json.JsonSerializer.Serialize(new
                {
                    disposition,
                    nextAttemptAt = at.AddHours(12)
                }), OccurredAt: at)).GetAwaiter().GetResult();
        }

        internal RunEventRecord? Read(string operation) =>
            Store.ReadLatestAsync(RunEventTypes.EvidenceRetention, operation).GetAwaiter().GetResult();

        internal RunEventMaintenanceCadenceResult Run(DateTimeOffset now,
            Func<DateTimeOffset, int> archive,
            Func<long, GoalSnapshotArchiveRecompressionResult> compression,
            RunEventMaintenanceResult? maintenance = null, Func<StorageRetentionResult>? retention = null) =>
            RunEventMaintenanceCadence.TryRunIfDue(Workspace.RunEventStorePath, Workspace.ConductEventsLogPath, () => now,
                maintenanceOperation: (_, _) => maintenance ?? CompletedMaintenance,
                workspace: Workspace,
                artifactRetentionOperation: (_, _, _, _) => retention is null ? SuccessfulSweep : retention(),
                goalSnapshotArchiveOperation: archive,
                archivedSnapshotCompressionOperation: compression,
                archivedSnapshotCompressionEnabled: true);

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
