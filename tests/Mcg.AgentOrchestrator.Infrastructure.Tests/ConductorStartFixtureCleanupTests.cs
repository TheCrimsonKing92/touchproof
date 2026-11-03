using System.Diagnostics;
using System.Text.Json;
using Xunit;

// Parallel-safe: uses the isolation fixture's unique root, named events and
// retained, start-time/image-validated conductor handle. No global enumeration.
public sealed class ConductorStartFixtureCleanupTests(ITestOutputHelper output)
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Theory(Skip = "Requires Windows process ownership.", SkipUnless = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public void RootRemoval_AfterOwnedConductorAcquisition_RequiresProcessExit(bool failStartup)
    {
        Process? conductor = null;
        string? root = null;
        string? identity = null;
        Task<string>? stdout = null;
        Task<string>? stderr = null;
        bool? exitedAtRemoval = null;
        var injectedFailure = new InvalidOperationException("Controlled failure after conductor acquisition.");
        try
        {
            var failure = Record.Exception(() =>
            {
                var fixture = new ConductorStartHandleIsolationTests.LaunchFixture(
                    afterCallerStarted: started =>
                    {
                        root = started.Root;
                        stdout = started.CallerOutput;
                        stderr = started.CallerError;
                        Assert.True(started.Caller.WaitForExit(60000), "Diagnostic caller did not exit.");
                        DrainCaller();
                        Assert.Equal(0, started.Caller.ExitCode);
                        Assert.True(started.Ready.WaitOne(0), "Caller exited without conductor readiness.");
                        conductor = started.OpenConductor();
                        identity = File.ReadAllText(Path.Combine(root, "launch-identity.json"));
                        Assert.False(conductor.HasExited);
                        WriteReceipt("owned-conductor-acquired", null);
                        if (failStartup) throw injectedFailure;
                    },
                    beforeRootRemoval: removing =>
                    {
                        // Observe BEFORE the diagnostic harness releases anything.
                        // Both normal disposal and constructor rollback must have
                        // completed the same owned-resource cleanup here.
                        try
                        {
                            Assert.True(Directory.Exists(removing.Root));
                            if (conductor is not null)
                            {
                                exitedAtRemoval = conductor.HasExited;
                                WriteReceipt("before-root-removal", exitedAtRemoval);
                            }
                        }
                        finally
                        {
                            // Harness containment, after recording the observation:
                            // release the real probe so RED leaves no live child and
                            // Root is deleted exactly once by the fixture itself.
                            removing.Release.Set();
                            if (conductor is not null)
                                Assert.True(conductor.WaitForExit(60000),
                                    "Validated diagnostic conductor did not exit after release.");
                            DrainCaller();
                            WriteReceipt("diagnostic-release-complete", conductor?.HasExited);
                        }
                    });
                fixture.Dispose();
            });

            if (failStartup) Assert.Same(injectedFailure, failure);
            else Assert.Null(failure);
            Assert.NotNull(root);
            Assert.False(Directory.Exists(root));
            WriteReceipt("root-removed", conductor?.HasExited);
            Assert.NotNull(exitedAtRemoval);
            // Original partial-startup rollback is RED at this same boundary;
            // the harness releases the conductor only after recording its state.
            Assert.True(exitedAtRemoval.Value,
                "Root removal began before the validated fixture-owned conductor exited; see lifecycle receipts.");
        }
        finally
        {
            conductor?.Dispose();
        }

        void DrainCaller()
        {
            Assert.NotNull(stdout);
            Assert.NotNull(stderr);
            Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10)).GetAwaiter().GetResult();
        }

        void WriteReceipt(string phase, bool? exited) => output.WriteLine(JsonSerializer.Serialize(new
        {
            phase,
            failStartup,
            root,
            pid = conductor?.Id,
            // OpenConductor validated both fields before retaining this handle.
            identity,
            exited
        }));
    }
}
