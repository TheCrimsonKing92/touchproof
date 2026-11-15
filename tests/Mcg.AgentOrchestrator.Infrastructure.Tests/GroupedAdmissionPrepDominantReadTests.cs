using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: private filesystem roots, fixed clock and injected holders; no child processes.
public sealed class GroupedAdmissionPrepDominantReadTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void DominantSubstep_TenAndThousandUnrelatedRecords_ReadsSameCount()
    {
        var small = MeasurePasses(10);
        var large = MeasurePasses(1000);

        Assert.Equal(small, large);
        // One marker and metadata read on restore; the next pass also re-reads the active run.
        Assert.Equal((1L, 1L, 1L, 2L), small);
    }

    private static (long FirstMarkers, long FirstRecords, long NextMarkers, long NextRecords) MeasurePasses(int history)
    {
        using var fixture = new Fixture();
        fixture.Save("held-a", "held-b");
        for (var index = 0; index < history; index++)
            fixture.SaveTerminal(fixture.Create($"unrelated-{index}-a", $"unrelated-{index}-b"));
        Assert.Equal(history + 1, fixture.Coordinator.ReadAll().Count());
        Assert.Single(Directory.GetFiles(fixture.MarkerFolder));

        var driver = Driver(fixture);
        var before = Counts(fixture.Coordinator);
        Assert.Equal(new[] { "held-a", "held-b" }, driver.GetActiveCohortGateMemberGoalIds().Order().ToArray());
        var first = Delta(fixture.Coordinator, before);
        before = Counts(fixture.Coordinator);
        Assert.Equal(new[] { "held-a", "held-b" }, driver.GetActiveCohortGateMemberGoalIds().Order().ToArray());
        var next = Delta(fixture.Coordinator, before);
        return (first.Markers, first.Records, next.Markers, next.Records);
    }

    [Fact]
    public void DominantSubstep_HeldLandedAndChangedInput_ReturnsLiteralExpectedResults()
    {
        using var fixture = new Fixture();
        fixture.Save("held-a", "held-b");
        fixture.SaveTerminal(fixture.Create("landed-a", "landed-b"));
        var changed = fixture.Save("changed-a", "changed-b");
        var driver = Driver(fixture);

        var before = Counts(fixture.Coordinator);
        Assert.Equal(new[] { "changed-a", "changed-b", "held-a", "held-b" },
            driver.GetActiveCohortGateMemberGoalIds().Order().ToArray());
        Assert.Equal((2L, 2L), Delta(fixture.Coordinator, before));

        fixture.SaveTerminal(changed);
        fixture.Save("new-a", "new-b");
        before = Counts(fixture.Coordinator);
        Assert.Equal(new[] { "held-a", "held-b", "new-a", "new-b" },
            driver.GetActiveCohortGateMemberGoalIds().Order().ToArray());
        Assert.Equal((2L, 4L), Delta(fixture.Coordinator, before));
    }

    [Fact]
    public void OpenAttempt_RestartThenTerminalSave_MarkerFollowsAttempt()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Coordinator.Launch(fixture.Create("held-a", "held-b"));
        Assert.True(File.Exists(fixture.Marker(attempt)));
        var restarted = fixture.NewCoordinator();
        Assert.Equal(attempt.AttemptId, Assert.Single(restarted.ReadOpen()).AttemptId);

        fixture.SaveTerminal(attempt);
        Assert.False(File.Exists(fixture.Marker(attempt)));
        Assert.Empty(restarted.ReadOpen());
        Assert.Equal(attempt.AttemptId, Assert.Single(restarted.ReadAll()).AttemptId);
    }

    [Fact]
    public void StartupScan_MarkerRemovedByHand_RestoresMarker()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Save("held-a", "held-b");
        fixture.SaveTerminal(fixture.Create("landed-a", "landed-b"));
        File.Delete(fixture.Marker(attempt));
        var restarted = fixture.NewCoordinator();
        Assert.Empty(restarted.ReadOpen());

        restarted.RecoverOpenAttemptMarkers();

        Assert.True(File.Exists(fixture.Marker(attempt)));
        Assert.Equal(attempt.AttemptId, Assert.Single(restarted.ReadOpen()).AttemptId);
        Assert.Contains("GROUPED_GATE_OPEN_SCAN scanned=2 open=1 created=1", fixture.Events);
        restarted.RecoverOpenAttemptMarkers();
        Assert.Contains("GROUPED_GATE_OPEN_SCAN scanned=2 open=1 created=0", fixture.Events);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpenReader_StaleMarker_RemovesAndReports(bool missingAttempt)
    {
        using var fixture = new Fixture();
        var attempt = fixture.Save("held-a", "held-b");
        if (missingAttempt) File.Delete(attempt.MetadataPath);
        else
        {
            fixture.SaveTerminal(attempt);
            // Crash residue: terminal metadata was published before the marker could be removed.
            File.WriteAllText(fixture.Marker(attempt), attempt.MetadataPath);
        }

        Assert.Empty(fixture.Coordinator.ReadOpen());
        Assert.False(File.Exists(fixture.Marker(attempt)));
        var reason = missingAttempt ? "missing-attempt" : "terminal-attempt";
        Assert.Equal($"GROUPED_GATE_OPEN_MARKER attempt={attempt.AttemptId} reason={reason} removed=true",
            Assert.Single(fixture.Events));
        Assert.Empty(fixture.Coordinator.ReadOpen());
        Assert.Single(fixture.Events);
    }

    [Fact]
    public void OpenReader_UnreconciledFinishedResult_KeepsMarkerUntilReconciliation()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Save("held-a", "held-b");
        fixture.Publish(attempt);

        Assert.Equal(attempt.AttemptId, Assert.Single(fixture.Coordinator.ReadOpen()).AttemptId);
        Assert.True(File.Exists(fixture.Marker(attempt)));
        File.Delete(attempt.ExitCodePath);
        File.Delete(attempt.ResultPath);
        Assert.True(fixture.Coordinator.ReservesCapacity(Assert.Single(fixture.Coordinator.ReadOpen())));
    }

    [Fact]
    public void OpenReader_MalformedAttempt_ThrowsWithoutRemovingMarker()
    {
        using var fixture = new Fixture();
        var attempt = fixture.Save("held-a", "held-b");
        File.WriteAllText(attempt.MetadataPath, "{");

        Assert.Throws<JsonException>(() => fixture.Coordinator.ReadOpen());
        Assert.True(File.Exists(fixture.Marker(attempt)));
    }

    private static ConductorDriver Driver(Fixture fixture)
    {
        var driver = MakeDriver(executionDirectory: fixture.Root, utcNow: () => DateTimeOffset.UnixEpoch);
        driver._groupedGateAttempts = fixture.Coordinator;
        return driver;
    }

    private static (long Markers, long Records) Counts(ConductorGroupedGateAttemptCoordinator coordinator) =>
        (coordinator.OpenMarkerEntriesRead, coordinator.AttemptRecordsRead);

    private static (long Markers, long Records) Delta(ConductorGroupedGateAttemptCoordinator coordinator,
        (long Markers, long Records) before) =>
        (coordinator.OpenMarkerEntriesRead - before.Markers, coordinator.AttemptRecordsRead - before.Records);

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"prep-dominant-{Guid.NewGuid():N}");
        internal string MarkerFolder => Path.Combine(Root, ConductorGroupedGateAttemptCoordinator.OpenAttemptsFolderName);
        internal List<string> Events { get; } = [];
        internal ConductorGroupedGateAttemptCoordinator Coordinator { get; }

        internal Fixture()
        {
            Directory.CreateDirectory(Root);
            Coordinator = NewCoordinator();
        }

        internal ConductorGroupedGateAttemptCoordinator NewCoordinator() => new(Root, generationId: 111,
            launch: _ => new(222, DateTimeOffset.UnixEpoch, @"C:\dotnet.exe"),
            isProcessAlive: pid => pid == 222, utcNow: () => DateTimeOffset.UnixEpoch, eventSink: Events.Add);

        internal ConductorGroupedGateAttempt Create(params string[] ids) => Coordinator.Create("cohort",
            ids.Select(id => new ConductorGroupedGateMember(id, new string('a', 40), new string('a', 40),
                ["src/" + id], [], ChangeRiskTier.Behavior).ToProjection(new string('b', 40))).ToArray(),
            new string('b', 40), "tree", "manifest", Guid.NewGuid().ToString("N"), Root,
            ConductorAutonomyPolicy.Conservative) with
        {
            OwnerProcessId = 222, OwnerProcessStartedAt = DateTimeOffset.UnixEpoch,
            OwnerExecutablePath = @"C:\dotnet.exe", AdoptedByGenerationId = 111
        };

        internal ConductorGroupedGateAttempt Save(params string[] ids)
        {
            var attempt = Create(ids);
            ConductorGroupedGateAttemptCoordinator.Save(attempt);
            return attempt;
        }

        internal string Marker(ConductorGroupedGateAttempt attempt) => Path.Combine(MarkerFolder, attempt.AttemptId);

        internal void Publish(ConductorGroupedGateAttempt attempt)
        {
            File.WriteAllText(attempt.ResultPath, "{\"Status\":\"completed\"}");
            File.WriteAllText(attempt.ExitCodePath, "0");
        }

        internal void SaveTerminal(ConductorGroupedGateAttempt attempt)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(attempt.MetadataPath)!);
            Publish(attempt);
            ConductorGroupedGateAttemptCoordinator.Save(attempt with
            {
                ReconciledAt = DateTimeOffset.UnixEpoch, Outcome = "Reconciled"
            });
        }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
