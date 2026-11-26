using Mcg.AgentOrchestrator.App.Cli;

public sealed class ShadowStartCopyPlanTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IncludesOnlyCopiedStoresAndGroupsTheirSidecarsByVolumeIdentity(bool separateStagingVolume)
    {
        using var fixture = new ShadowStartCommandTests.Fixture();
        var source = Path.Combine(fixture.Source, "state.db");
        WriteLength(source, 10 * ShadowStartCopyPlan.MiB);
        WriteLength(source + "-wal", 200);
        WriteLength(source + "-shm", 300);
        WriteLength(source + "-journal", 400);
        WriteLength(Path.Combine(fixture.Source, "backlog.db-wal"), 1000);
        WriteLength(Path.Combine(fixture.Source, "impact-replay.checkpoint.db"), 1000);
        var state = fixture.Scratch("scratch");
        var hooks = fixture.Hooks with
        {
            MapVolume = path => path == fixture.Staging
                ? new(separateStagingVolume ? "temp" : "scratch", "temp-alias")
                : new("scratch", "scratch-root")
        };

        var plan = ShadowStartCopyPlan.Create(fixture.Source, state, fixture.Staging, fixture.Registry,
            ShadowScratchRetention.Disposable, hooks);

        Assert.Equal(ShadowScratchRetention.Disposable, plan.Retention);
        Assert.Equal(new ShadowStartStore(source, Path.Combine(state, "state.db"), Path.Combine(fixture.Staging, "state.db")),
            Assert.Single(plan.Stores));
        var expectedStaging = 10 * ShadowStartCopyPlan.MiB + 900;
        var expectedScratch = ShadowStartCopyPlan.Reserve + ShadowStartCopyPlan.WorkingSpace +
            36 * ShadowStartCopyPlan.MiB + 900;
        Assert.Equal(expectedScratch + (separateStagingVolume ? 0 : expectedStaging), plan.Capacity[0].RequiredBytes);
        Assert.Equal("scratch-root", plan.Capacity[0].Volume);
        if (separateStagingVolume)
            Assert.Equal(new ShadowStartCapacity("temp-alias", ShadowStartCopyPlan.Reserve + expectedStaging, long.MaxValue),
                Assert.Single(plan.Capacity.Skip(1)));
        else
            Assert.Single(plan.Capacity);
    }

    private static void WriteLength(string path, long length)
    {
        using var file = File.Create(path);
        file.SetLength(length);
    }
}
