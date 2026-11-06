using Mcg.AgentOrchestrator.Infrastructure;
using Support = ProcessTreeGuiSuppressionTestsHandleListSupport;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsWorkerJobConsolePolicy
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Fact(Skip = "Requires Windows native process attachment.", SkipUnless = nameof(IsWindows))]
    public void AttachedProcess_PreservesExitCodeAfterWait()
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        var name = "Local\\mcg-attach-exit-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, name);
        HandleListLaunchedProcess? native = null;
        try
        {
            native = HandleListProcessLauncher.Start(Support.Probe(root, "wait", name));
            using var process = native.AttachProcess();
            release.Set();
            Assert.True(process.WaitForExit(120_000), "Attached child did not exit after release.");
            Assert.Equal(native.ExitCode, process.ExitCode);
            Assert.Equal(0, process.ExitCode);
        }
        finally
        {
            release.Set();
            Support.Stop(native);
            Directory.Delete(root, true);
        }
    }

    [Xunit.Fact(Skip = "Requires Windows dispatch streams.", SkipUnless = nameof(IsWindows))]
    public void DispatchStreams_PreservePromptAndEnvironment()
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        try
        {
            var prompt = Path.Combine(root, "prompt.md");
            var content = System.Text.Encoding.UTF8.GetBytes("dispatch prompt 漢字 🙂");
            File.WriteAllBytes(prompt, content);
            var stdout = Path.Combine(root, "stdout.log");
            var stderr = Path.Combine(root, "stderr.log");
            var parameters = new DispatchProcessHost.DispatchRunParameters(
                "$source = [Console]::OpenStandardInput(); $bytes = [IO.MemoryStream]::new(); " +
                "$source.CopyTo($bytes); [Console]::Out.Write([Convert]::ToBase64String($bytes.ToArray()) + ':' + " +
                $"$env:{WorkerSandboxOptions.DispatchWorkerVariable}); [Console]::Error.Write('dispatch-stderr')",
                root, stdout, stderr, Path.Combine(root, "exit.txt"), null,
                DisableSharedCompilation: false, Provider: WorkerSandboxProvider.Codex, PromptPath: prompt);
            var parametersPath = Path.Combine(root, "dispatch.json");
            DispatchProcessHost.WriteParameters(parametersPath, parameters);
            var result = DispatchProcessHost.Run(parametersPath);
            var output = File.ReadAllText(stdout);
            var error = File.ReadAllText(stderr);
            Assert.True(result == 0, $"Dispatch exit={result}; stdout={output}; stderr={error}");
            Assert.Equal(Convert.ToBase64String(content) + ":1", output);
            Assert.Equal("dispatch-stderr", error);
        }
        finally { Directory.Delete(root, true); }
    }

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
