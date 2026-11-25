using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

public sealed class ShadowStartCrashRecoveryTests
{
    [Fact]
    public void DeadPreLaunchOwnerReclaimsOnlyRegisteredCopies()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ShadowStartCommandTests.Fixture();
        var run = Seed(fixture, "orphan");
        var diagnostic = Put(fixture.Scratch("orphan"), "diagnostic.txt");
        var unrelated = Put(run.State, "unrelated.db");
        var unplannedStore = Put(run.State, "backlog.db");
        var retained = Put(fixture.Scratch("retained"), "state.db");
        var source = Put(fixture.Source, "state.db");
        using var registry = new ShadowStartRunRegistry(fixture.Registry);
        var diagnostics = new StringWriter();

        registry.Recover(fixture.Source, diagnostics);

        Assert.Empty(diagnostics.ToString());
        foreach (var suffix in ShadowStartCopyPlan.Suffixes)
            Assert.False(File.Exists(Path.Combine(run.State, "state.db" + suffix)));
        Assert.False(Directory.Exists(run.Staging));
        Assert.All(new[] { diagnostic, unrelated, unplannedStore, retained, source }, path => Assert.Equal("fixture", File.ReadAllText(path)));
        Assert.True(File.Exists(registry.ReceiptPath(run.Id)));
    }

    [Fact]
    public void RecoveryDefersLiveOwnerAndAbsentJobButRetriesAfterExclusiveHandleCloses()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ShadowStartCommandTests.Fixture();
        var live = Seed(fixture, "live", liveOwner: true);
        var unknownJob = Seed(fixture, "running", phase: ShadowStartPhase.Running);
        var released = Seed(fixture, "released", phase: ShadowStartPhase.Released);
        using var registry = new ShadowStartRunRegistry(fixture.Registry);
        var first = new StringWriter();
        using (File.Open(Path.Combine(released.State, "state.db-wal"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            registry.Recover(fixture.Source, first);

            Assert.Contains("cleanup_reason=sharing-violation", first.ToString());
            Assert.True(File.Exists(Path.Combine(released.State, "state.db")));
            Assert.True(File.Exists(Path.Combine(released.Staging, "state.db")));
        }
        Assert.Contains("cleanup_reason=supervisor-live-or-unverifiable", first.ToString());
        Assert.Contains("cleanup_reason=process-group-unconfirmed", first.ToString());

        registry.Recover(fixture.Source, new StringWriter());

        Assert.True(File.Exists(Path.Combine(live.State, "state.db")));
        Assert.True(File.Exists(Path.Combine(unknownJob.State, "state.db")));
        Assert.False(File.Exists(Path.Combine(released.State, "state.db")));
        Assert.Equal(ShadowStartPhase.Reclaimed,
            JsonSerializer.Deserialize<ShadowStartRun>(File.ReadAllText(registry.RecordPath(released.Id)))!.Phase);
    }

    [Fact]
    public async Task RecoveryUsesSettlementAfterTheOuterJobClosesAndCustodianExits()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ShadowStartCommandTests.Fixture();
        fixture.CreateDatabase("state.db");
        var run = Seed(fixture, "settled-job", phase: ShadowStartPhase.Running, reserveRuntimeSidecars: true);
        var database = Path.Combine(run.State, "state.db");
        var start = new ProcessStartInfo("powershell.exe")
        {
            WorkingDirectory = run.Scratch, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File",
                fixture.CandidateScriptPath, "hold-wal", "no-marker", "conduct", "--no-dispatch",
                "--disposable-fixture", "--repository-root", run.Scratch, "--expected-state-directory", run.State }
        };
        start.Environment["MCG_ORCHESTRATOR_REPOSITORY_ROOT"] = run.Scratch;
        using var registry = new ShadowStartRunRegistry(fixture.Registry);
        using var candidate = OwnedProcessGroup.StartSuspendedContainedRedirected(start);
        var identity = candidate.ReadSuspendedIdentity();
        using var custody = DurableAcceptanceJob.Attach(candidate, run.JobName,
            AcceptanceJobCustodianHost.BindShadowStart(run.Scratch, run.State), registry.SettlementPath(run.Id));
        using var observer = Process.GetProcessById(custody.CustodianIdentity.ProcessId);
        _ = observer.SafeHandle;
        candidate.Resume();
        Assert.Equal("wal-ready", await candidate.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15)));
        Assert.True(new FileInfo(database + "-wal").Length > 32);
        Assert.True(new FileInfo(database + "-shm").Length > 0);

        // Closing the sole outer-job handle exercises the kill-on-close path used on supervisor death.
        candidate.Group.Dispose();
        Assert.True(observer.WaitForExit(15000), "The job custodian did not settle and exit.");
        Assert.Equal(0, observer.ExitCode);
        Assert.True(File.Exists(registry.SettlementPath(run.Id)));
        var recorded = run with
        {
            CandidatePid = identity.ProcessId,
            CandidateStarted = identity.StartedAt,
            CandidateImagePath = identity.ImagePath
        };
        registry.Save(recorded with { CandidateStarted = identity.StartedAt.AddMinutes(-1) });
        var mismatch = new StringWriter();
        registry.Recover(fixture.Source, mismatch);
        Assert.Contains("cleanup_reason=process-group-unconfirmed", mismatch.ToString());
        Assert.True(File.Exists(database));
        Assert.True(File.Exists(database + "-wal"));

        registry.Save(recorded);
        var diagnostics = new StringWriter();

        registry.Recover(fixture.Source, diagnostics);

        Assert.Empty(diagnostics.ToString());
        Assert.False(File.Exists(database));
        Assert.False(File.Exists(database + "-wal"));
        Assert.False(File.Exists(database + "-shm"));
        Assert.False(Directory.Exists(run.Staging));
    }

    [Fact]
    public void UnreservedSidecarDefersBeforeDeletingRegisteredCopies()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ShadowStartCommandTests.Fixture();
        var run = Seed(fixture, "unreserved-sidecar");
        var unrelated = Put(run.Staging, "state.db-wal");
        using var registry = new ShadowStartRunRegistry(fixture.Registry);
        var diagnostics = new StringWriter();

        registry.Recover(fixture.Source, diagnostics);

        Assert.Contains("Store file identity mismatch", diagnostics.ToString());
        Assert.Equal("fixture", File.ReadAllText(unrelated));
        Assert.True(File.Exists(Path.Combine(run.State, "state.db")));
        Assert.True(File.Exists(Path.Combine(run.Staging, "state.db")));
    }

    [Fact]
    public void SamePathReplacementDefersBeforeDeletingAnyRegisteredStore()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ShadowStartCommandTests.Fixture();
        var run = Seed(fixture, "replacement-file");
        var replaced = Path.Combine(run.State, "state.db-wal");
        File.Move(replaced, Path.Combine(run.State, "original-wal"));
        File.WriteAllText(replaced, "replacement");
        using var registry = new ShadowStartRunRegistry(fixture.Registry);
        var diagnostics = new StringWriter();

        registry.Recover(fixture.Source, diagnostics);

        Assert.Contains("Store file identity mismatch", diagnostics.ToString());
        Assert.Equal("replacement", File.ReadAllText(replaced));
        Assert.True(File.Exists(Path.Combine(run.State, "state.db")));
        Assert.True(File.Exists(Path.Combine(run.Staging, "state.db")));
    }

    [Fact]
    public void ReplacementDirectoryAndRecordPointingAtSourceCannotAuthorizeDeletion()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ShadowStartCommandTests.Fixture();
        var replaced = Seed(fixture, "replaced");
        Directory.Move(replaced.Scratch, fixture.Scratch("original"));
        var foreign = Put(replaced.State, "state.db");
        var intoSource = Seed(fixture, "misdirected");
        var sourceState = Path.Combine(fixture.Source, ".orchestrator");
        var live = Put(sourceState, "state.db");
        using var registry = new ShadowStartRunRegistry(fixture.Registry);
        registry.Save(intoSource with { Scratch = fixture.Source, State = sourceState });
        var diagnostics = new StringWriter();

        registry.Recover(fixture.Source, diagnostics);

        Assert.Contains("Directory identity mismatch", diagnostics.ToString());
        Assert.Contains("Invalid disposable ownership record", diagnostics.ToString());
        Assert.True(File.Exists(foreign));
        Assert.True(File.Exists(live));
        Assert.True(File.Exists(Path.Combine(replaced.Staging, "state.db")));
    }

    internal static ShadowStartRun Seed(ShadowStartCommandTests.Fixture fixture, string name, bool liveOwner = false,
        ShadowStartPhase phase = ShadowStartPhase.Copying, bool reserveRuntimeSidecars = false)
    {
        var id = Guid.NewGuid();
        var scratch = fixture.Scratch(name);
        var state = Path.Combine(scratch, ".orchestrator");
        var staging = Path.Combine(fixture.Staging, id.ToString("N"));
        var stateFiles = reserveRuntimeSidecars ? [Put(state, "state.db")] :
            ShadowStartCopyPlan.Suffixes.Select(suffix => Put(state, "state.db" + suffix)).ToArray();
        if (reserveRuntimeSidecars)
            File.Copy(Path.Combine(fixture.Source, "state.db"), stateFiles[0], overwrite: true);
        var stores = stateFiles
            .Append(Put(staging, "state.db")).ToDictionary(path => path, path =>
            {
                using var file = File.OpenRead(path);
                return ShadowStartReclaimer.ReadIdentity(file.SafeFileHandle);
            });
        using var owner = Process.GetCurrentProcess();
        var run = new ShadowStartRun(id, ShadowScratchRetention.Disposable, fixture.Source, scratch, state, staging,
            ["state.db"], liveOwner ? owner.Id : int.MaxValue, owner.StartTime.ToUniversalTime(), phase,
            ShadowStartReclaimer.CaptureDirectory(scratch), ShadowStartReclaimer.CaptureDirectory(state),
            ShadowStartReclaimer.CaptureDirectory(staging), CandidatePid: int.MaxValue - 1,
            CandidateStarted: owner.StartTime.ToUniversalTime(), StoreIdentities: stores,
            SidecarSlots: reserveRuntimeSidecars ? ShadowStartReclaimer.ReserveSidecarSlots(stateFiles[0]) : null);
        using var registry = new ShadowStartRunRegistry(fixture.Registry);
        registry.Save(run);
        return run;
    }

    private static string Put(string directory, string name)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, name);
        File.WriteAllText(path, "fixture");
        return path;
    }
}
