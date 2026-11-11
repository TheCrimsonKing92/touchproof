using System.Reflection;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsGitRevisionReaderConsolePolicy
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Theory(Skip = "Requires Windows child console policy.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void PersistentReader_AppliesConsolePolicyAndPreservesProtocol(bool inherit)
    {
        var root = ProcessTreeGuiSuppressionTestsProcessErrorMode.CreateRoot();
        var previousExperiment = ChildConsoleLaunchPolicy.ExperimentForTests;
        var previousHook = ChildConsoleLaunchPolicy.PrepareDelayHookForTests;
        GitRevisionReader.ReaderProcess? reader = null;
        try
        {
            Assert.True(GitCli.Run(root, "init", "-q").Succeeded);
            ChildConsoleLaunchPolicy.ExperimentForTests = inherit
                ? ChildConsoleExperiment.InheritWindowlessConsole : ChildConsoleExperiment.Off;
            var expected = ChildConsoleLaunchPolicy.Prepare().ChildCreationFlags |
                HandleListProcessLauncher.UnicodeEnvironment | HandleListProcessLauncher.ExtendedStartup;
            var preparations = 0;
            ChildConsoleLaunchPolicy.PrepareDelayHookForTests = () => preparations++;

            reader = GitRevisionReader.ReaderProcess.Start(Path.Combine(root, ".git"));
            var process = reader.process;
            Assert.Equal(expected, process.EffectiveCreationFlags);
            // Also detects a skipped Prepare on hosts where both policies select the same flags.
            Assert.Equal(1, preparations);
            Assert.Equal("HEAD missing", reader.Query("HEAD", 120_000));
        }
        finally
        {
            ChildConsoleLaunchPolicy.PrepareDelayHookForTests = previousHook;
            ChildConsoleLaunchPolicy.ExperimentForTests = previousExperiment;
            reader?.Terminate();
            Directory.Delete(root, true);
        }
    }
}
