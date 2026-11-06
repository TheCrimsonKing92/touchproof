using Mcg.AgentOrchestrator.Infrastructure;
using Support = ProcessTreeGuiSuppressionTestsHandleListSupport;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsGitCliDedicatedDrain
{
    public static bool IsWindows => OperatingSystem.IsWindows();
    [Xunit.Fact(Skip = "Requires Windows native pipes.", SkipUnless = nameof(IsWindows))]
    public void SaturatedPool_BothLargeStreamsDrainOnDedicatedThreads()
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        Exception? failure = null;
        var info = Support.Probe(root, "streams");
        var thread = new Thread(() =>
        {
            var previous = GitCli.StartInfoObserver;
            try
            {
                GitCli.StartInfoObserver = start =>
                {
                    Support.PinEnvironment(start, root);
                    start.StandardOutputEncoding = start.StandardErrorEncoding = System.Text.Encoding.UTF8;
                };
                BoundedThreadPoolSaturationApparatus.RunWithBoundedSaturatedThreadPool(evidence =>
                {
                    ThreadPool.GetAvailableThreads(out var available, out _);
                    Assert.Equal(0, available);
                    var result = GitCli.RunExecutable(info.FileName, root, 120_000, info.ArgumentList.ToArray());
                    Assert.True(result.Succeeded, result.Error);
                    Assert.False(result.DrainTimedOut, result.Error);
                    Assert.Equal(1024 * 1026, result.Output.Length);
                    Assert.Equal(1024 * 1026, result.Error.Length);
                });
            }
            catch (Exception ex) { failure = ex; }
            finally { GitCli.StartInfoObserver = previous; }
        }) { IsBackground = true };
        try
        {
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromMinutes(4)), "Saturated two-stream launch did not finish.");
            Assert.Null(failure);
        }
        finally { Directory.Delete(root, true); }
    }
}
