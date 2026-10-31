using Mcg.AgentOrchestrator.Infrastructure;

[Collection(TestCollections.ProcessSpawning)]
public sealed class IcaclsIntegrityLabelerTestsAcceptanceGateIntegrityLevelTests
{
    [Fact]
    public async Task Normalize_LowWorkspace_NewExecutableRunsMediumAndWritesTemp()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        using var fixture = new AcceptanceGateIntegrityTestSupport();
        await fixture.AssertMediumHostAsync();
        Assert.True(fixture.Labels.SetIntegrity(fixture.Worktree, "(OI)(CI)L", recursive: true));
        Assert.True(fixture.Labels.Query(fixture.Worktree).Low);
        Assert.True(fixture.Labels.Query(fixture.Worktree).Inheritable);

        AcceptanceGateIntegrityNormalizer.Normalize(fixture.Worktree);

        Assert.True(fixture.Labels.Query(fixture.Worktree).Medium);
        await fixture.AssertMediumProbeAsync(fixture.Worktree);
    }

    [Fact]
    public async Task Normalize_ExistingLowExecutable_ControlFailsThenPreparedProbePasses()
    {
        AcceptanceGateIntegrityTestSupport.RequireWindows();
        using var fixture = new AcceptanceGateIntegrityTestSupport();
        await fixture.AssertMediumHostAsync();
        Assert.True(fixture.Labels.SetIntegrity(fixture.Worktree, "(OI)(CI)L", recursive: true));
        var executable = fixture.CopyExecutable(fixture.Worktree);
        Assert.True(fixture.Labels.Query(executable).Low);

        var (control, deniedDirectory) = await fixture.ProbeAsync(fixture.Worktree, executable);
        Assert.False(control.TimedOut);
        Assert.Equal(73, control.ExitCode);
        Assert.Contains(AcceptanceGateIntegrityTestSupport.LowSid, control.Output);
        Assert.DoesNotContain(AcceptanceGateIntegrityTestSupport.MediumSid, control.Output);
        Assert.False(Directory.Exists(deniedDirectory));

        AcceptanceGateIntegrityNormalizer.Normalize(fixture.Worktree);

        Assert.True(fixture.Labels.Query(executable).Medium);
        var (prepared, created) = await fixture.ProbeAsync(fixture.Worktree, executable);
        Assert.False(prepared.TimedOut);
        Assert.Equal(0, prepared.ExitCode);
        Assert.Contains(AcceptanceGateIntegrityTestSupport.MediumSid, prepared.Output);
        Assert.True(Directory.Exists(created));
        Assert.Equal("gate-write-ok", File.ReadAllText(Path.Combine(created, "receipt.txt")).Trim());
    }
}
