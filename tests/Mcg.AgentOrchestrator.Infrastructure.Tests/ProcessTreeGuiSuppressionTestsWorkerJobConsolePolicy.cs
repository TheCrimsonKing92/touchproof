using Mcg.AgentOrchestrator.Infrastructure;
using Support = ProcessTreeGuiSuppressionTestsHandleListSupport;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsWorkerJobConsolePolicy
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    [Xunit.Theory(Skip = "Requires Windows job and console policy.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void WorkerCreation_EntersJobWithPreservedConsoleFlags(bool inherit, bool own)
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        var name = "Local\\mcg-worker-policy-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        var previous = ChildConsoleLaunchPolicy.ExperimentForTests;
        using var group = OwnedProcessGroup.Create();
        HandleListLaunchedProcess? process = null;
        try
        {
            ChildConsoleLaunchPolicy.ExperimentForTests = inherit ? ChildConsoleExperiment.InheritWindowlessConsole : ChildConsoleExperiment.Off;
            var expected = ChildConsoleLaunchPolicy.Prepare(own).ChildCreationFlags;
            var observedAtCreation = false;
            process = group.StartRedirected(Support.Probe(root, "wait", name), own, createdObserver: child =>
            {
                Assert.True(group.Contains(child.SafeHandle), "Worker was outside its job at the CreateProcess return seam.");
                observedAtCreation = true;
            });
            Assert.True(observedAtCreation);
            Assert.True(group.Contains(process.SafeHandle), "Worker was not assigned to its creation job.");
            Assert.Equal(expected | HandleListProcessLauncher.UnicodeEnvironment | HandleListProcessLauncher.ExtendedStartup,
                process.EffectiveCreationFlags);
            Assert.False(process.HasExited);
            group.Dispose();
            Assert.True(process.WaitForExit(120_000), "Worker did not exit when its job closed.");
            // The pure table covers console states independently of this host's console attachment.
            Assert.Equal(0u, ChildConsoleLaunchPolicy.Select(ChildConsoleExperiment.InheritWindowlessConsole, false, () => true).ChildCreationFlags);
            Assert.Equal(ChildConsoleLaunchPolicy.CreateNoWindow,
                ChildConsoleLaunchPolicy.Select(ChildConsoleExperiment.Off, false, () => true).ChildCreationFlags);
            Assert.Equal(0u, ChildConsoleLaunchPolicy.Select(ChildConsoleExperiment.Off, true, () => true).ChildCreationFlags);
        }
        finally
        {
            release.Set();
            Support.Stop(process);
            ChildConsoleLaunchPolicy.ExperimentForTests = previous;
            Directory.Delete(root, true);
        }
    }
}
