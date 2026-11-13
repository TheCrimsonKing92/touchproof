using System.Diagnostics;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique repository, release marker, job and history; no global observer swaps.
public sealed class GitCliDescendantProcessorTimeTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Windows job accounting and Git for Windows.", SkipUnless = nameof(IsWindows))]
    public void DefaultPath_ChildConsumesCpu_ReportsCombinedProcessorTime()
    {
        var root = Directory.CreateTempSubdirectory("git-descendant-cpu-").FullName;
        var release = Path.Combine(root, "release");
        using var hangDetector = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var releasedByHangDetector = 0;
        using var registration = hangDetector.Token.Register(() =>
        {
            Interlocked.Exchange(ref releasedByHangDetector, 1);
            File.WriteAllText(release, "release");
        });
        HandleListLaunchedProcess? launched = null;
        Process? ownedProcess = null;
        var sawDescendantCpu = false;
        try
        {
            // The alias shell is a descendant; git itself waits. Release is driven by the CPU
            // observation, so no wall-clock pacing decides whether the assertion succeeds.
            var result = GitCli.RunProgressExecutable("git", root,
                ["-c", "alias.cpu-probe=!echo child-ready; while test ! -f release; do :; done", "cpu-probe"],
                new GitCli.DurationHistory(), observation =>
                {
                    Assert.NotNull(launched);
                    Assert.NotNull(observation.ProcessorTime);
                    if (observation.ProcessorTime > launched.TotalProcessorTime)
                    {
                        sawDescendantCpu = true;
                        File.WriteAllText(release, "release");
                    }
                }, started: child =>
                {
                    launched = child;
                    ownedProcess = child.AttachProcess();
                },
                configureStart: start => PinEnvironment(start, root));
            Assert.True(result.Succeeded, result.Error);
            Assert.Contains("child-ready", result.Output);
            Assert.True(sawDescendantCpu, "No combined CPU sample exceeded the launched git process's own CPU.");
            Assert.Equal(0, Volatile.Read(ref releasedByHangDetector));
        }
        finally
        {
            registration.Dispose();
            if (ownedProcess is not null)
            {
                try
                {
                    if (!ownedProcess.HasExited) ownedProcess.Kill(entireProcessTree: true);
                    Assert.True(ownedProcess.WaitForExit(120_000), "The owned git CPU probe did not exit.");
                }
                finally { ownedProcess.Dispose(); }
            }
            SharedTestSupport.RemoveTempDirectory(root);
        }
    }

    [Fact]
    public void DefaultPath_NaturalExits_PreserveOutputAndPopulateHistory()
    {
        var root = Directory.CreateTempSubdirectory("git-progress-exit-").FullName;
        try
        {
            var history = new GitCli.DurationHistory();
            var success = GitCli.RunProgressExecutable("git", root, ["--version"], history,
                configureStart: start => PinEnvironment(start, root));
            Assert.True(success.Succeeded, success.Error);
            Assert.StartsWith("git version ", success.Output);
            Assert.Equal(string.Empty, success.Error);
            Assert.NotNull(history.Window(string.Empty));

            var failure = GitCli.RunProgressExecutable("git", root, ["mcg-no-such-progress-command"], history,
                configureStart: start => PinEnvironment(start, root));
            Assert.NotEqual(0, failure.ExitCode);
            Assert.NotEqual(-1, failure.ExitCode);
            Assert.Contains("mcg-no-such-progress-command", failure.Error);
            Assert.NotNull(history.Window("mcg-no-such-progress-command"));
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    private static void PinEnvironment(ProcessStartInfo start, string root)
    {
        string[] allowed = ["SystemRoot", "SystemDrive", "WINDIR", "PATH", "PATHEXT", "COMSPEC", "TEMP", "TMP"];
        start.Environment.Clear();
        foreach (var name in allowed)
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["HOME"] = root;
        start.Environment["USERPROFILE"] = root;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = OperatingSystem.IsWindows() ? "NUL" : "/dev/null";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
    }
}
