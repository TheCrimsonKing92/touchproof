using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Support = ProcessTreeGuiSuppressionTestsHandleListSupport;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsGitCliStreamContract
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Theory(Skip = "Requires Windows native pipes.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData("streams")]
    [Xunit.InlineData("exit")]
    [Xunit.InlineData("echo")]
    public void NativeStreams_MatchManagedResult(string mode)
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        var prior = GitCli.StartInfoObserver;
        try
        {
            var info = Support.Probe(root, mode);
            if (mode == "echo")
                foreach (var arg in new[] { "a b", "a\"b", "trailing " + (char)92, "", "λ" }) info.ArgumentList.Add(arg);
            // GitCli preserves CreateNoWindow=true. The policy wrapper can instead inherit
            // a console, changing the probe's default encoding and thus the emitted bytes.
            using var managed = ProcessTreeGuiSuppression.StartWithLaunchLock(info);
            Assert.NotNull(managed);
            var output = PipeDrain.Start(managed.StandardOutput, "oracle-out");
            var error = PipeDrain.Start(managed.StandardError, "oracle-err");
            Assert.True(managed.WaitForExit(120_000), "Managed stream oracle did not exit.");
            Assert.True(output.Join(Environment.TickCount64 + 120_000), "Managed stdout did not drain.");
            Assert.True(error.Join(Environment.TickCount64 + 120_000), "Managed stderr did not drain.");
            GitCli.StartInfoObserver = start => ProcessTreeGuiSuppressionTestsProcessErrorMode.PinEnvironment(start, root);
            var actual = GitCli.RunExecutable(info.FileName, root, 120_000, info.ArgumentList.ToArray());
            Assert.True(actual.ProcessStarted, actual.Error);
            Assert.False(actual.DrainTimedOut, actual.Error);
            Assert.Equal(managed.ExitCode, actual.ExitCode);
            Assert.Equal(output.Text, actual.Output);
            Assert.Equal(error.Text, actual.Error);
            if (mode == "streams") { Assert.True(actual.Output.Length > 1024 * 1024); Assert.True(actual.Error.Length > 1024 * 1024); }
        }
        finally { GitCli.StartInfoObserver = prior; Directory.Delete(root, true); }
    }

    [Xunit.Fact(Skip = "Requires Windows git and native stdin.", SkipUnless = nameof(IsWindows))]
    public void GitInputAndNonzeroExit_PreserveCallerResults()
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        var prior = GitCli.StartInfoObserver;
        try
        {
            GitCli.StartInfoObserver = start => ProcessTreeGuiSuppressionTestsProcessErrorMode.PinEnvironment(start, root);
            var content = new string('x', 128 * 1024);
            var bytes = Encoding.UTF8.GetBytes(content);
            var blob = Encoding.ASCII.GetBytes($"blob {bytes.Length}\0").Concat(bytes).ToArray();
            var expected = Convert.ToHexString(SHA1.HashData(blob)).ToLowerInvariant();
            var result = GitCli.RunWithStandardInput(root, 120_000, content, "hash-object", "--stdin");
            Assert.True(result.Succeeded, result.Error);
            Assert.Equal(expected, result.Output.Trim());
            var failure = GitCli.Run(root, "mcg-no-such-command");
            Assert.True(failure.ProcessStarted);
            Assert.NotEqual(0, failure.ExitCode);
            Assert.Contains("mcg-no-such-command", failure.Error);
        }
        finally { GitCli.StartInfoObserver = prior; Directory.Delete(root, true); }
    }

    [Xunit.Fact(Skip = "Requires Windows process-tree termination.", SkipUnless = nameof(IsWindows))]
    public void Timeout_KillsRootAndGrandchild()
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        var key = "Local\\mcg-tree-" + Guid.NewGuid().ToString("N");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, key);
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, key + "-ready");
        var pidFile = Path.Combine(root, "child.pid");
        Process? grandchild = null;
        Process? parent = null;
        ConductorSupervisorProcessIdentity? parentIdentity = null, grandchildIdentity = null;
        var prior = GitCli.StartInfoObserver;
        try
        {
            var info = Support.Probe(root, "tree", key, pidFile, key + "-ready");
            GitCli.StartInfoObserver = start => ProcessTreeGuiSuppressionTestsProcessErrorMode.PinEnvironment(start, root);
            var result = GitCli.RunExecutable(info.FileName, root, 1000, info.ArgumentList.ToArray(), null, null,
                process =>
                {
                    parent = process.AttachProcess();
                    _ = parent.SafeHandle;
                    parentIdentity = TestOwnedProcessStop.Identify(parent);
                    Assert.True(ready.WaitOne(TimeSpan.FromSeconds(120)), "Grandchild did not signal that its identity was written.");
                    grandchild = Process.GetProcessById(int.Parse(File.ReadAllText(pidFile)));
                    _ = grandchild.SafeHandle;
                    grandchildIdentity = TestOwnedProcessStop.Identify(grandchild);
                });
            Assert.Equal(-1, result.ExitCode);
            Assert.Contains("timed out after", result.Error);
            Assert.NotNull(parent); Assert.NotNull(grandchild);
            Assert.True(parent.WaitForExit(120_000), "Timed-out root did not exit.");
            Assert.True(grandchild.WaitForExit(120_000), "Timed-out grandchild did not exit.");
        }
        finally
        {
            GitCli.StartInfoObserver = prior;
            release.Set();
            TestOwnedProcessStop.StopTreeIfSame(parentIdentity);
            TestOwnedProcessStop.StopTreeIfSame(grandchildIdentity);
            foreach (var process in new[] { parent, grandchild })
                if (process is not null) process.Dispose();
            Directory.Delete(root, true);
        }
    }
}
