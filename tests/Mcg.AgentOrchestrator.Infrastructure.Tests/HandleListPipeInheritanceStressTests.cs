using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;
using Support = HandleListPipeInheritanceReproductionTests.Support;
using Probes = ProcessTreeGuiSuppressionTestsHandleListSupport;

public sealed class HandleListPipeInheritanceStressTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Theory(Skip = "Requires Windows handle inheritance.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData("raw", false)]
    [Xunit.InlineData("legacy", false)]
    [Xunit.InlineData("raw", true)]
    [Xunit.InlineData("launcher", false)]
    public void ConcurrentPairs_UnrelatedChildRemainsAlive_OutputReachesEof(string creator, bool contained)
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        try
        {
            for (var pair = 0; pair < 100; pair++) RunPair(root, creator, contained);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static void RunPair(string root, string creator, bool contained)
    {
        using var release = Support.ReleaseEvent(out var releaseName);
        using var start = new Barrier(2);
        var failures = new ConcurrentQueue<Exception>();
        HandleListLaunchedProcess? output = null, nativeHolder = null;
        OwnedProcessGroup.SuspendedRedirectedProcessStart? owned = null;
        Process? managedHolder = null;
        Support.Drains? outputDrains = null, holderDrains = null;
        var outputThread = new Thread(() =>
        {
            try
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(60)), "Pair did not reach output launch barrier.");
                var info = Support.OutputCommand(root, waitForInput: true);
                if (contained)
                {
                    owned = OwnedProcessGroup.StartSuspendedContainedRedirected(info);
                    owned.Resume();
                }
                else output = HandleListProcessLauncher.Start(info);
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        }) { IsBackground = true };
        var holderThread = new Thread(() =>
        {
            try
            {
                Assert.True(start.SignalAndWait(TimeSpan.FromSeconds(60)), "Pair did not reach holder launch barrier.");
                var info = Probes.Probe(root, "wait", releaseName);
                switch (creator)
                {
                    case "raw": managedHolder = Process.Start(info)!; break;
                    case "legacy": managedHolder = ProcessTreeGuiSuppression.StartWithLaunchLock(info)!; break;
                    case "launcher": nativeHolder = HandleListProcessLauncher.Start(info); break;
                    default: throw new ArgumentException("Unknown inheritance-capable creator.", nameof(creator));
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
        }) { IsBackground = true };
        outputThread.Start();
        holderThread.Start();
        try
        {
            Assert.True(outputThread.Join(TimeSpan.FromSeconds(60)), "Output creator did not return.");
            Assert.True(holderThread.Join(TimeSpan.FromSeconds(60)), "Holder creator did not return.");
            Assert.Empty(failures);
            Assert.True(managedHolder is not null || nativeHolder is not null, "Pair did not create a holder.");
            outputDrains = new(owned?.StandardOutput ?? output!.StandardOutput, owned?.StandardError ?? output!.StandardError);
            holderDrains = new(managedHolder?.StandardOutput ?? nativeHolder!.StandardOutput,
                managedHolder?.StandardError ?? nativeHolder!.StandardError);
            Assert.False(managedHolder?.HasExited ?? nativeHolder!.HasExited);
            // The output child cannot exit until BOTH creations have returned; no wall-clock pacing.
            (owned?.StandardInput ?? output!.StandardInput).WriteLine("release");
            Assert.True(owned?.Process.WaitForExit(60_000) ?? output!.WaitForExit(60_000), "Output child did not exit after input release.");
            outputDrains.AssertEof(Support.Output, Support.Error);
            Assert.Equal(0, owned?.Process.ExitCode ?? output!.ExitCode);
            Assert.False(managedHolder?.HasExited ?? nativeHolder!.HasExited);
            release.Set();
            Assert.True(managedHolder?.WaitForExit(60_000) ?? nativeHolder!.WaitForExit(60_000), "Released holder did not exit.");
            holderDrains.AssertEof("", "");
            Assert.Equal(0, managedHolder?.ExitCode ?? nativeHolder!.ExitCode);
        }
        finally
        {
            release.Set();
            Assert.True(outputThread.Join(TimeSpan.FromSeconds(60)), "Output creator did not finish during cleanup.");
            Assert.True(holderThread.Join(TimeSpan.FromSeconds(60)), "Holder creator did not finish during cleanup.");
            Support.Stop(managedHolder);
            Probes.Stop(nativeHolder);
            owned?.Dispose();
            Probes.Stop(output);
            outputDrains?.JoinAfterCleanup();
            holderDrains?.JoinAfterCleanup();
        }
    }

    [Xunit.Fact(Skip = "Requires the Windows runtime creation monitor.", SkipUnless = nameof(IsWindows))]
    public void RuntimeLock_RawStartWaitsForRelease()
    {
        var runtimeField = typeof(Process).GetField("s_createProcessLock", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(runtimeField);
        Assert.Same(runtimeField.GetValue(null), HandleListProcessLauncher.InheritanceLock);
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        using var release = Support.ReleaseEvent(out var releaseName);
        using var attempting = new ManualResetEventSlim();
        using var completed = new ManualResetEventSlim();
        Process? child = null;
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var info = Probes.Probe(root, "wait", releaseName);
                attempting.Set();
                child = Process.Start(info);
            }
            catch (Exception ex) { failure = ex; }
            finally { completed.Set(); }
        }) { IsBackground = true };
        try
        {
            lock (HandleListProcessLauncher.InheritanceLock)
            {
                thread.Start();
                Assert.True(attempting.Wait(TimeSpan.FromSeconds(60)), "Raw creator did not attempt Process.Start.");
                Assert.True(SpinWait.SpinUntil(() => completed.IsSet ||
                    (thread.ThreadState & System.Threading.ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(60)),
                    "Raw creator did not reach the held runtime monitor.");
                Assert.False(completed.IsSet);
                Assert.Null(child);
            }
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Raw creator did not finish after monitor release.");
            Assert.Null(failure);
            Assert.NotNull(child);
            var drains = new Support.Drains(child.StandardOutput, child.StandardError);
            release.Set();
            Assert.True(child.WaitForExit(60_000), "Lock-contract child did not exit after release.");
            drains.AssertEof("", "");
            Assert.Equal(0, child.ExitCode);
        }
        finally
        {
            release.Set();
            Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Raw creator did not finish during cleanup.");
            Support.Stop(child);
            Directory.Delete(root, recursive: true);
        }
    }
}
