using System.Collections.Concurrent;
using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsGitCliCaptureExclusion
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Theory(Skip = "Requires Windows inheritable capture handles.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public void NativeCapture_ConcurrentGitChildrenCannotRetainCapture(bool sharedPolicy)
    {
        // The bypass case is the negative control: the same live git commands retain the file.
        const int count = 3;
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        var output = Path.Combine(root, "native.out");
        var error = Path.Combine(root, "native.err");
        var key = $"Local\\mcg-capture-{Guid.NewGuid():N}";
        var releaseName = key + "-release";
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, releaseName);
        using var attempts = new CountdownEvent(count);
        using var launched = new CountdownEvent(count);
        var ready = Enumerable.Range(0, count).Select(i =>
            new EventWaitHandle(false, EventResetMode.ManualReset, key + $"-ready-{i}")).ToArray();
        var children = new Process?[count];
        var failures = new ConcurrentQueue<Exception>();
        var threads = Enumerable.Range(0, count).Select(index => new Thread(() =>
        {
            var previousObserver = GitCli.StartInfoObserver;
            try
            {
                GitCli.StartInfoObserver = start => ProcessTreeGuiSuppressionTestsProcessErrorMode.PinEnvironment(start, root);
                var alias = "!" + ShellQuote(ProcessTreeGuiSuppressionTestsProcessErrorMode.ProbeExecutable().Replace('\\', '/')) +
                    " --hold-for-event " + ShellQuote(key + $"-ready-{index}") + " " + ShellQuote(releaseName);
                string[] args = ["-c", "alias.mcg-capture-hold=" + alias, "mcg-capture-hold"];
                if (sharedPolicy)
                {
                    var result = GitCli.RunExecutable("git", root, 600_000, args, null,
                        () => attempts.Signal(), child =>
                        {
                            children[index] = child;
                            launched.Signal();
                        });
                    Assert.True(result.Succeeded, result.Error);
                }
                else
                {
                    // Deliberately bypass the policy to prove the exclusive-open oracle detects capture.
                    var result = InfrastructureTestSupport.RunGitProbe(root, args, startProcess: child =>
                    {
                        ProcessTreeGuiSuppressionTestsProcessErrorMode.PinEnvironment(child.StartInfo, root);
                        GitCli.ApplyLaunchSettings(child.StartInfo, readCommand: true);
                        var didStart = child.Start();
                        if (didStart)
                        {
                            children[index] = child;
                            launched.Signal();
                            attempts.Signal();
                        }
                        return didStart;
                    });
                    Assert.True(result.Succeeded, result.ToString());
                }
            }
            catch (Exception ex) { failures.Enqueue(ex); }
            finally { GitCli.StartInfoObserver = previousObserver; }
        }) { IsBackground = true }).ToArray();
        var started = false;
        try
        {
            var nativeStart = ProcessTreeGuiSuppressionTestsProcessErrorMode.ExitCommand();
            ProcessTreeGuiSuppressionTestsProcessErrorMode.PinEnvironment(nativeStart, root);
            using (var native = OwnedProcessGroup.StartSuspendedWithFileCapture(
                nativeStart, output, error,
                inheritableWindowObserver: () =>
                {
                    foreach (var thread in threads) thread.Start();
                    started = true;
                    Assert.True(attempts.Wait(TimeSpan.FromSeconds(60)),
                        "Git attempts did not reach the native inheritable-handle window.");
                    if (sharedPolicy)
                        Assert.Equal(count, launched.CurrentCount); // Every git attempt is blocked at the lock.
                }))
            {
                native.Resume();
                Assert.True(native.WaitForOwnedExit(TimeSpan.FromSeconds(60)), "Native capture child did not exit.");
            }

            Assert.True(launched.Wait(TimeSpan.FromSeconds(60)), "Git children did not start after the window closed.");
            foreach (var signal in ready)
                Assert.True(signal.WaitOne(TimeSpan.FromSeconds(60)), "Git alias probe did not signal readiness.");
            Assert.Empty(failures);
            foreach (var child in children)
            {
                Assert.NotNull(child);
                Assert.False(child.HasExited, "Git child must still be alive during the exclusive-open check.");
            }
            foreach (var path in new[] { output, error })
            {
                if (sharedPolicy)
                {
                    using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Assert.True(exclusive.CanRead);
                }
                else
                {
                    Assert.Throws<IOException>(() =>
                    {
                        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    });
                }
            }
        }
        finally
        {
            release.Set();
            if (started)
            {
                foreach (var thread in threads)
                {
                    if (thread.Join(TimeSpan.FromSeconds(60))) continue;
                    // Exact children captured at creation are the only permitted cleanup targets.
                    foreach (var child in children)
                    {
                        try { if (child is not null && !child.HasExited) child.Kill(entireProcessTree: true); }
                        catch (InvalidOperationException) { }
                    }
                    Assert.True(thread.Join(TimeSpan.FromSeconds(60)), "Git launch thread did not finish after release/cleanup.");
                }
            }
            foreach (var signal in ready) signal.Dispose();
            Directory.Delete(root, recursive: true);
        }
        Assert.Empty(failures);
    }

    private static string ShellQuote(string text) => "'" + text.Replace("'", "'\\''") + "'";
}
