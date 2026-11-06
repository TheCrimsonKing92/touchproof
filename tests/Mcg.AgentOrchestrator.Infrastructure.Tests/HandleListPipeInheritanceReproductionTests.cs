using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;
using Support = HandleListPipeInheritanceReproductionTests.Support;
using Probes = ProcessTreeGuiSuppressionTestsHandleListSupport;

public sealed class HandleListPipeInheritanceReproductionTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Fact(Skip = "Requires Windows handle inheritance.", SkipUnless = nameof(IsWindows))]
    public void ChildExited_RawHolderStillAlive_BothDrainsReachEof()
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        using var release = Support.ReleaseEvent(out var releaseName);
        using var childCreated = new ManualResetEventSlim();
        using var holderCreated = new ManualResetEventSlim();
        Process? holder = null;
        HandleListLaunchedProcess? child = null;
        Support.Drains? drains = null, holderDrains = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.True(childCreated.Wait(TimeSpan.FromSeconds(60)), "Output child creation was not signaled.");
                holder = Process.Start(Probes.Probe(root, "wait", releaseName))!;
            }
            catch (Exception ex) { failure = ex; }
            finally { holderCreated.Set(); }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            child = HandleListProcessLauncher.Start(Support.OutputCommand(root), createdObserver: _ =>
            {
                // On base the parent's child pipe ends remain inheritable throughout this callback.
                // Force a raw managed start on another thread to capture them before Start returns.
                childCreated.Set();
                Assert.True(holderCreated.Wait(TimeSpan.FromSeconds(60)), "Raw holder creation did not finish inside the observer.");
                Assert.Null(failure);
            });
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Raw holder creator did not finish.");
            Assert.NotNull(holder);
            holderDrains = new(holder.StandardOutput, holder.StandardError);
            drains = new(child.StandardOutput, child.StandardError);
            Assert.False(holder.HasExited);
            Assert.True(child.WaitForExit(60_000), "Output child did not exit.");
            drains.AssertEof(Support.Output, Support.Error);
            Assert.Equal(0, child.ExitCode);
            Assert.False(holder.HasExited); // Holder release happens only after both EOF assertions.
            release.Set();
            Assert.True(holder.WaitForExit(60_000), "Released raw holder did not exit.");
            holderDrains.AssertEof("", "");
            Assert.Equal(0, holder.ExitCode);
        }
        finally
        {
            childCreated.Set();
            release.Set();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Raw holder creator did not finish during cleanup.");
            Support.Stop(holder);
            Probes.Stop(child);
            drains?.JoinAfterCleanup();
            holderDrains?.JoinAfterCleanup();
            Directory.Delete(root, recursive: true);
        }
    }
    // All roots, events, processes and drains belong to one test. No environment or telemetry
    // globals are changed, so these tests need no exclusive process-spawning collection.
    internal static class Support
    {
        internal const string Output = "0123456789012345678901234567890123456789\r\n";
        internal const string Error = "probe-error\r\n";

        internal static ProcessStartInfo OutputCommand(string root, bool waitForInput = false)
        {
            var info = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "cmd.exe"))
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                Arguments = "/d /q /c \"echo 0123456789012345678901234567890123456789&1>&2 echo probe-error" +
                    (waitForInput ? "&set /p release=" : "") + "\""
            };
            ProcessTreeGuiSuppressionTestsHandleListSupport.PinEnvironment(info, root);
            return info;
        }

        internal static EventWaitHandle ReleaseEvent(out string name)
        {
            name = "Local\\mcg-pipe-eof-" + Guid.NewGuid().ToString("N");
            return new EventWaitHandle(false, EventResetMode.ManualReset, name);
        }

        internal static void Stop(Process? process)
        {
            if (process is null) return;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                Assert.True(process.WaitForExit(60_000), "Fixture process did not exit during cleanup.");
            }
            finally { process.Dispose(); }
        }

        internal sealed class Drains
        {
            private readonly PipeDrain output, error;

            internal Drains(StreamReader stdout, StreamReader stderr)
            {
                output = PipeDrain.Start(stdout, "pipe-inheritance-stdout");
                error = PipeDrain.Start(stderr, "pipe-inheritance-stderr");
            }

            // Call immediately after observing child exit. The bound detects a missing EOF event;
            // it is never used to schedule either launch or to release the unrelated holder.
            internal void AssertEof(string expectedOutput, string expectedError)
            {
                var deadline = Environment.TickCount64 + 2000;
                var stdoutEof = output.Join(deadline);
                var stderrEof = error.Join(deadline);
                Assert.True(stdoutEof, "stdout EOF did not arrive within 2000 ms of child exit: " + output.Describe());
                Assert.True(stderrEof, "stderr EOF did not arrive within 2000 ms of child exit: " + error.Describe());
                Assert.DoesNotContain("failure=", output.Describe());
                Assert.DoesNotContain("failure=", error.Describe());
                Assert.Equal(expectedOutput, output.Text);
                Assert.Equal(expectedError, error.Text);
            }

            internal void JoinAfterCleanup()
            {
                var deadline = Environment.TickCount64 + 60_000;
                Assert.True(output.Join(deadline), "stdout drain did not stop after fixture children exited.");
                Assert.True(error.Join(deadline), "stderr drain did not stop after fixture children exited.");
            }
        }
    }

}
