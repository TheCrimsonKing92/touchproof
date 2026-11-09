using Mcg.AgentOrchestrator.App.OwnerConsole;

// Parallel-safe: source, markers, clock and temporary store files are per test.
public sealed class OwnerConsoleDecisionReuseTests
{
    [Fact]
    public async Task MarkerChangeAndForcedReadRefreshDecisions_StableMarkerReuses()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        fixture.Source.Items.Add(new("q1", "11111111", OwnerQuestionKind.HumanInput, "First?"));
        await fixture.Builder.ReadDecisionsAsync(CancellationToken.None);
        await fixture.Builder.ReadDecisionsAsync(CancellationToken.None);
        Assert.Equal(1, fixture.Source.Reads);
        fixture.Source.Items.Add(new("q2", "22222222", OwnerQuestionKind.HumanInput, "Second?"));
        fixture.Marker = "changed";
        var changed = await fixture.Builder.ReadDecisionsAsync(CancellationToken.None);
        Assert.Equal(2, changed.Decisions.Length);
        Assert.Equal(2, fixture.Source.Reads);
        await fixture.Builder.ReadDecisionsAsync(CancellationToken.None, force: true);
        Assert.Equal(3, fixture.Source.Reads);
    }

    [Fact]
    public async Task OwnerCommandsInvokeEveryLoaderAndForceDecisions_WhileSnapshotsAndScanReuse()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture();
        var refresh = fixture.Coordinator();
        await refresh.RefreshAsync();
        await refresh.RefreshAsync();
        Assert.Equal(2, fixture.Source.Reads);
        Assert.Equal(2, fixture.State.Boards);
        Assert.Equal(2, fixture.LifecycleReads);
        Assert.Equal(2, fixture.TickReads);
        Assert.Equal(2, fixture.GateReads);
        Assert.Equal(1, fixture.ScanReads);
        Assert.Equal(2, fixture.State.Loads.Count);
        Assert.All(fixture.State.Loads.Values, loads => Assert.Equal(1, loads));
    }

    [Fact]
    public async Task MissingMarkerAlwaysReads_AndFailedReadDoesNotAcceptMarker()
    {
        using var fixture = new OwnerConsoleRefreshReuseFixture(marker: false);
        await fixture.Builder.ReadDecisionsAsync(CancellationToken.None);
        await fixture.Builder.ReadDecisionsAsync(CancellationToken.None);
        Assert.Equal(2, fixture.Source.Reads);
        using var marked = new OwnerConsoleRefreshReuseFixture();
        await marked.Builder.ReadDecisionsAsync(CancellationToken.None);
        marked.Marker = "new";
        marked.Source.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => marked.Builder.ReadDecisionsAsync(CancellationToken.None));
        marked.Source.Fail = false;
        await marked.Builder.ReadDecisionsAsync(CancellationToken.None);
        Assert.Equal(3, marked.Source.Reads);
    }

    [Fact]
    public void FingerprintIncludesBothStoresAndTheirWalFiles()
    {
        var directory = Directory.CreateTempSubdirectory("owner-fingerprint-");
        try
        {
            var state = Path.Combine(directory.FullName, "state.db");
            var collaboration = Path.Combine(directory.FullName, "collaboration-items.db");
            var fingerprint = OwnerConsoleStoreFingerprint.For(state, collaboration);
            foreach (var path in new[] { state, state + "-wal", collaboration, collaboration + "-wal" })
            {
                var before = fingerprint();
                File.WriteAllText(path, "write");
                Assert.NotNull(before);
                Assert.NotEqual(before, fingerprint());
                var created = fingerprint();
                File.AppendAllText(path, "another write");
                Assert.NotEqual(created, fingerprint());
                File.Delete(path);
                Assert.Equal(before, fingerprint());
            }
        }
        finally { directory.Delete(recursive: true); }
    }
}
