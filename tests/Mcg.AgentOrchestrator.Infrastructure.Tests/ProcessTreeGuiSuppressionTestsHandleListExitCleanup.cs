using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsHandleListExitCleanup
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Theory(Skip = "Requires a Windows native process handle.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Kill_WhenManagedLookupLosesProcess_UsesRetainedNativeHandle(bool exitsDuringLookup)
    {
        // set /p stays alive until its redirected input receives a line or closes.
        using var child = HandleListProcessLauncher.Start(new ProcessStartInfo("cmd.exe", "/d /q /c set /p cleanup=")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        });
        try
        {
            Assert.False(child.HasExited);
            var lookups = 0;
            child.Kill(entireProcessTree: true, attachProcess: () =>
            {
                lookups++;
                if (exitsDuringLookup)
                {
                    child.StandardInput.WriteLine("done");
                    Assert.True(child.WaitForExit(120_000), "Cleanup probe did not finish reading input.");
                }
                throw new ArgumentException("The process is absent from managed PID lookup.");
            });
            Assert.Equal(1, lookups);
            Assert.True(child.WaitForExit(120_000), "Retained-handle cleanup did not terminate the probe.");
            var exitCode = child.ExitCode;
            if (!exitsDuringLookup) Assert.Equal(-1, exitCode);
            child.Kill(entireProcessTree: true);
            Assert.Equal(exitCode, child.ExitCode);
        }
        finally { ProcessTreeGuiSuppressionTestsHandleListSupport.Stop(child); }
    }

    [Xunit.Theory(Skip = "Requires a Windows native process handle.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Kill_AfterExit_PreservesExitCodeAndSupportsRepeatedCleanup(bool entireProcessTree)
    {
        using var child = HandleListProcessLauncher.Start(new ProcessStartInfo("cmd.exe", "/d /c exit 17")
            { UseShellExecute = false, CreateNoWindow = true });
        try
        {
            Assert.True(child.WaitForExit(120_000), "Cleanup probe did not exit.");
            child.Kill(entireProcessTree);
            child.Kill(entireProcessTree);
            Assert.Equal(17, child.ExitCode);
        }
        finally { ProcessTreeGuiSuppressionTestsHandleListSupport.Stop(child); }
    }
}
