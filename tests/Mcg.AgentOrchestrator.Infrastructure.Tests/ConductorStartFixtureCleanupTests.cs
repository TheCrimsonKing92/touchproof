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
        int? exitCodeAtRemoval = null;
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
                                if (exitedAtRemoval.Value) exitCodeAtRemoval = conductor.ExitCode;
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
            Assert.Equal(0, exitCodeAtRemoval);
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
            exited,
            exitCode = conductor is { HasExited: true } ? conductor.ExitCode : (int?)null
        }));
    }

    [Theory(Skip = "Requires Windows process ownership.", SkipUnless = nameof(IsWindows))]
    [InlineData(false)]
    [InlineData(true)]
    public void RootRemoval_OnReleaseWatchdogFailure_PreservesRootAndFailure(bool failStartup)
    {
        Process? conductor = null;
        string? root = null;
        var removalReached = false;
        var watchdogTriggered = false;
        var injectedFailure = new InvalidOperationException("Controlled failure after conductor acquisition.");
        try
        {
            var failure = Record.Exception(() =>
            {
                var fixture = new ConductorStartHandleIsolationTests.LaunchFixture(
                    afterCallerStarted: started =>
                    {
                        root = started.Root;
                        Assert.True(started.Caller.WaitForExit(60000), "Diagnostic caller did not exit.");
                        Assert.Equal(0, started.Caller.ExitCode);
                        Assert.True(started.Ready.WaitOne(0), "Caller exited without conductor readiness.");
                        conductor = started.OpenConductor();
                        Assert.False(conductor.HasExited);
                        if (failStartup) throw injectedFailure;
                    },
                    beforeRootRemoval: _ => removalReached = true,
                    beforeConductorRelease: (releasing, owned) =>
                    {
                        Assert.False(owned.HasExited);
                        // Select the real probe's watchdog-failure branch with an
                        // event, rather than shortening or waiting out its deadline.
                        releasing.ReleaseWatchdogExpired.Set();
                        watchdogTriggered = true;
                    });
                fixture.Dispose();
            });

            Assert.True(watchdogTriggered, "Controlled watchdog branch was not selected.");
            Assert.NotNull(conductor);
            Assert.True(conductor.HasExited);
            Assert.NotEqual(0, conductor.ExitCode);
            var cleanupFailure = Assert.IsType<AggregateException>(failure);
            if (failStartup)
            {
                Assert.Same(injectedFailure, cleanupFailure.InnerExceptions[0]);
                cleanupFailure = Assert.IsType<AggregateException>(cleanupFailure.InnerExceptions[1]);
            }
            var completionFailure = Assert.IsType<InvalidOperationException>(
                Assert.Single(cleanupFailure.InnerExceptions));
            Assert.Equal(
                $"Test-owned fake conductor did not complete cooperatively; exit code: {conductor.ExitCode}.",
                completionFailure.Message);
            Assert.False(removalReached, "Root removal must not begin after failed cooperative completion.");
            Assert.NotNull(root);
            Assert.True(Directory.Exists(root));
            Assert.True(File.Exists(Path.Combine(root, "launch-identity.json")));
            using var receipt = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "launch-receipt.json")));
            Assert.Contains("Fake conductor release signal absent.",
                File.ReadAllText(receipt.RootElement.GetProperty("stderrPath").GetString()!));
            output.WriteLine(JsonSerializer.Serialize(new
            {
                phase = "watchdog-failure-preserved",
                failStartup,
                root,
                identity = File.ReadAllText(Path.Combine(root, "launch-identity.json")),
                exitCode = conductor.ExitCode,
                removalReached
            }));
        }
        finally
        {
            // Contain the diagnostic root only after the retained, validated child
            // has exited. Fixture cleanup has already closed its pipes and events.
            if (conductor is { HasExited: true } && root is not null && Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            conductor?.Dispose();
        }
    }
}
