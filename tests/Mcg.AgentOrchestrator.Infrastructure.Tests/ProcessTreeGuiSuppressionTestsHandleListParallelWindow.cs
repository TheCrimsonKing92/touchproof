using Mcg.AgentOrchestrator.Infrastructure;
using Support = ProcessTreeGuiSuppressionTestsHandleListSupport;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsHandleListParallelWindow
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    [Xunit.Theory(Skip = "Requires Windows native creation.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void HeldWindow_AnotherThreadCompletesCreation(bool git)
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        using var inWindow = new ManualResetEventSlim();
        using var secondCreated = new ManualResetEventSlim();
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Assert.True(inWindow.Wait(TimeSpan.FromSeconds(120)), "First launch did not enter its inheritable window.");
                if (git)
                {
                    var result = GitCli.RunExecutable("git", root, 120_000, ["--version"], null, null,
                        _ => secondCreated.Set());
                    Assert.True(result.Succeeded, result.Error);
                }
                else
                {
                    using var child = HandleListProcessLauncher.Start(ProcessTreeGuiSuppressionTestsProcessErrorMode.ExitCommand());
                    secondCreated.Set();
                    Assert.True(child.WaitForExit(120_000), "Second launch did not exit.");
                }
            }
            catch (Exception ex) { failure = ex; }
        }) { IsBackground = true };
        thread.Start();
        try
        {
            using var first = OwnedProcessGroup.StartSuspendedWithFileCapture(
                ProcessTreeGuiSuppressionTestsProcessErrorMode.ExitCommand(), Path.Combine(root, "out"), Path.Combine(root, "err"),
                () =>
                {
                    inWindow.Set();
                    Assert.True(secondCreated.Wait(TimeSpan.FromSeconds(120)), "Second CreateProcess did not complete while the first window was held.");
                });
            first.Resume();
            Assert.True(first.WaitForOwnedExit(TimeSpan.FromSeconds(120)), "First launch did not exit.");
        }
        finally
        {
            inWindow.Set();
            Assert.True(thread.Join(TimeSpan.FromSeconds(180)), "Second launch thread did not finish.");
            Directory.Delete(root, true);
        }
        Assert.Null(failure);
    }
}
