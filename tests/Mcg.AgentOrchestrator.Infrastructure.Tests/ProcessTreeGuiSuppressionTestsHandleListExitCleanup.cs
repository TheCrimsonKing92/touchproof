using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsHandleListExitCleanup
{
    public static bool IsWindows => OperatingSystem.IsWindows();

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
