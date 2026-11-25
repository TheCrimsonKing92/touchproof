using System.Diagnostics;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
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
    public void RecoveryCanReclaimWhenTheExactLaunchedJobStillReportsZeroActiveProcesses()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new ShadowStartCommandTests.Fixture();
        var run = Seed(fixture, "settled-job", phase: ShadowStartPhase.Running);
        using var group = OwnedProcessGroup.CreateNamedContained(run.JobName);
        var start = new ProcessStartInfo("cmd.exe")
        {
            WorkingDirectory = run.Scratch, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "/d", "/c", "exit", "0" }
        };
        using var candidate = group.StartRedirected(start);
        var candidateStarted = candidate.StartTime.ToUniversalTime();
        Assert.True(candidate.WaitForExit(TimeSpan.FromSeconds(15)), "The settled-job fixture candidate did not exit.");
        using var registry = new ShadowStartRunRegistry(fixture.Registry);
        registry.Save(run with { CandidatePid = candidate.Id, CandidateStarted = candidateStarted });
        var diagnostics = new StringWriter();

        registry.Recover(fixture.Source, diagnostics);

        Assert.Empty(diagnostics.ToString());
        Assert.False(File.Exists(Path.Combine(run.State, "state.db")));
        Assert.False(Directory.Exists(run.Staging));
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
        ShadowStartPhase phase = ShadowStartPhase.Copying)
    {
        var id = Guid.NewGuid();
        var scratch = fixture.Scratch(name);
        var state = Path.Combine(scratch, ".orchestrator");
        var staging = Path.Combine(fixture.Staging, id.ToString("N"));
        foreach (var suffix in ShadowStartCopyPlan.Suffixes)
            Put(state, "state.db" + suffix);
        Put(staging, "state.db");
        using var owner = Process.GetCurrentProcess();
        var run = new ShadowStartRun(id, ShadowScratchRetention.Disposable, fixture.Source, scratch, state, staging,
            ["state.db"], liveOwner ? owner.Id : int.MaxValue, owner.StartTime.ToUniversalTime(), phase,
            ShadowStartReclaimer.CaptureDirectory(scratch), ShadowStartReclaimer.CaptureDirectory(state),
            ShadowStartReclaimer.CaptureDirectory(staging), CandidatePid: int.MaxValue - 1,
            CandidateStarted: owner.StartTime.ToUniversalTime());
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
