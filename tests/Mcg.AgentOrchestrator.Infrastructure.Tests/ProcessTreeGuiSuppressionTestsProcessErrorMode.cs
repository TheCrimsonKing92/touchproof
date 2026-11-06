using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Mcg.AgentOrchestrator.Infrastructure;

[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class ProcessTreeGuiSuppressionTestsProcessErrorMode
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Xunit.Fact]
    public void Initialization_PreservesExistingFlagsAndWritesOnce()
    {
        uint mode = 0x4;
        var writes = 0;
        var initialized = 0;
        var access = new ProcessTreeGuiSuppression.ErrorModeAccess(() => mode, value =>
        {
            writes++;
            var before = mode;
            mode = value;
            return before;
        });

        ProcessTreeGuiSuppression.InitializeProcessErrorMode(access, ref initialized);
        ProcessTreeGuiSuppression.InitializeProcessErrorMode(access, ref initialized);

        Assert.Equal(1, writes);
        Assert.Equal(0x4u | ProcessTreeGuiSuppression.SuppressedErrorModeFlags, mode);
    }

    [Xunit.Theory(Skip = "Requires Windows error-mode inheritance.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData("git")]
    [Xunit.InlineData("start")]
    [Xunit.InlineData("native")]
    public void Startup_ChildInheritsSuppressionOnEveryLaunchPath(string path)
    {
        // Do not initialize here: this assertion proves the assembly startup hook ran.
        AssertSuppressed(GetErrorMode());
        var root = CreateRoot();
        var previousObserver = GitCli.StartInfoObserver;
        try
        {
            GitCli.StartInfoObserver = start => ProcessTreeGuiSuppressionTestsHandleListSupport.PinEnvironment(start, root);
            var receipt = Path.Combine(root, "mode.txt");
            var start = new ProcessStartInfo(ProbeExecutable()) { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("--report-error-mode");
            start.ArgumentList.Add(receipt);
            ProcessTreeGuiSuppressionTestsHandleListSupport.PinEnvironment(start, root);
            if (path == "git")
            {
                var result = GitCli.RunExecutable(start.FileName, root, 60_000,
                    "--report-error-mode", receipt);
                Assert.True(result.Succeeded, result.Error);
            }
            else if (path == "native")
            {
                using var launch = OwnedProcessGroup.StartSuspended(start);
                launch.Resume();
                Assert.True(launch.WaitForOwnedExit(TimeSpan.FromSeconds(60)), "Mode probe did not exit its owned job.");
                Assert.Equal(0, launch.TryReadOwnedExitCode());
            }
            else
            {
                using var child = ProcessTreeGuiSuppression.Start(start);
                try
                {
                    Assert.True(child.WaitForExit(60_000), "Mode probe did not exit.");
                    Assert.Equal(0, child.ExitCode);
                }
                finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
            }
            Assert.True(File.Exists(receipt), "Mode probe did not publish its error-mode receipt.");
            AssertSuppressed(uint.Parse(File.ReadAllText(receipt), CultureInfo.InvariantCulture));
        }
        finally
        {
            GitCli.StartInfoObserver = previousObserver;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Theory(Skip = "Requires Windows launch policy.", SkipUnless = nameof(IsWindows))]
    [Xunit.InlineData(false, "console")]
    [Xunit.InlineData(false, "error-mode")]
    [Xunit.InlineData(false, "suppressed")]
    [Xunit.InlineData(false, "start")]
    [Xunit.InlineData(false, "git")]
    [Xunit.InlineData(false, "native")]
    [Xunit.InlineData(false, "capture")]
    [Xunit.InlineData(true, "console")]
    [Xunit.InlineData(true, "error-mode")]
    [Xunit.InlineData(true, "suppressed")]
    [Xunit.InlineData(true, "start")]
    [Xunit.InlineData(true, "git")]
    [Xunit.InlineData(true, "native")]
    [Xunit.InlineData(true, "capture")]
    public void Launches_WriteNoModeUnlessFlagsNeedOnePersistentRepair(bool clearFlags, string path)
    {
        uint mode = clearFlags ? 0x4u : 0x4u | ProcessTreeGuiSuppression.SuppressedErrorModeFlags;
        var writes = 0;
        using var diagnostics = new StringWriter();
        var access = new ProcessTreeGuiSuppression.ErrorModeAccess(() => mode, value =>
        {
            writes++;
            var before = mode;
            mode = value;
            return before;
        }, diagnostics.WriteLine);
        var previousObserver = GitCli.StartInfoObserver;
        var root = CreateRoot();
        try
        {
            GitCli.StartInfoObserver = start => PinEnvironment(start, root);
            for (var i = 0; i < 2; i++)
            {
                var start = ExitCommand();
                PinEnvironment(start, root);
                switch (path)
                {
                    case "console":
                        using (ProcessTreeGuiSuppression.AcquireConsoleForChildSpawn(access)) { }
                        break;
                    case "error-mode":
                        using (ProcessTreeGuiSuppression.AcquireErrorModeForChildSpawn(access)) { }
                        break;
                    case "suppressed":
                        using (ProcessTreeGuiSuppression.AcquireSuppressedChildSpawn(false, access)) { }
                        break;
                    case "start":
                        using (var child = ProcessTreeGuiSuppression.Start(start, access))
                        {
                            try { Assert.True(child.WaitForExit(60_000), "Managed counting probe did not exit."); }
                            finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
                        }
                        break;
                    case "git":
                        var git = GitCli.RunExecutable("git", root, 60_000, ["--version"], access, null);
                        Assert.True(git.Succeeded, git.Error);
                        break;
                    case "native":
                    case "capture":
                        using (var launch = path == "native"
                            ? OwnedProcessGroup.StartSuspended(start, access)
                            : OwnedProcessGroup.StartSuspendedWithFileCapture(start,
                                Path.Combine(root, $"out-{i}"), Path.Combine(root, $"err-{i}"), errorModeAccess: access))
                        {
                            launch.Resume();
                            Assert.True(launch.WaitForOwnedExit(TimeSpan.FromSeconds(60)), "Native counting probe did not exit.");
                        }
                        break;
                    default: throw new ArgumentOutOfRangeException(nameof(path));
                }
                Assert.Equal(clearFlags ? 1 : 0, writes);
            }
            Assert.Equal(0x4u | ProcessTreeGuiSuppression.SuppressedErrorModeFlags, mode);
            var lines = diagnostics.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
            if (clearFlags)
                Assert.StartsWith("PROCESS_ERROR_MODE_REPAIRED entry=", Assert.Single(lines));
            else
                Assert.Empty(lines);
        }
        finally
        {
            GitCli.StartInfoObserver = previousObserver;
            Directory.Delete(root, recursive: true);
        }
    }

    [Xunit.Fact(Skip = "Requires Windows launch policy.", SkipUnless = nameof(IsWindows))]
    public void RepairFailure_ReportsFailureAndStillCreatesChild()
    {
        using var diagnostics = new StringWriter();
        var access = new ProcessTreeGuiSuppression.ErrorModeAccess(() => 0,
            _ => throw new InvalidOperationException("injected repair failure"), diagnostics.WriteLine);
        using var child = ProcessTreeGuiSuppression.Start(ExitCommand(), access);
        try
        {
            Assert.True(child.WaitForExit(60_000), "Repair-failure probe did not exit.");
            Assert.Equal(0, child.ExitCode);
            Assert.Contains("PROCESS_ERROR_MODE_REPAIR_FAILED entry=Start", diagnostics.ToString());
        }
        finally { if (!child.HasExited) child.Kill(entireProcessTree: true); }
    }

    internal static string ProbeExecutable()
    {
        const string name = "Mcg.AgentOrchestrator.ConsoleIoProbe.exe";
        var directory = FixtureProbeLocator.FindDirectory(typeof(ProcessTreeGuiSuppressionTestsProcessErrorMode),
            "Mcg.AgentOrchestrator.ConsoleIoProbe", name);
        Assert.True(directory is not null, "Console I/O probe apphost was not built.");
        return Path.Combine(directory!, name);
    }

    internal static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), $"mcg-error-mode-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        return root;
    }

    internal static ProcessStartInfo ExitCommand() => new("cmd.exe", "/d /c exit 0")
        { UseShellExecute = false, CreateNoWindow = true };

    internal static void PinEnvironment(ProcessStartInfo start, string root)
    {
        string[] allowed = ["SystemRoot", "SystemDrive", "WINDIR", "PATH", "PATHEXT", "COMSPEC",
            "TEMP", "TMP", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT(x86)"];
        start.Environment.Clear();
        foreach (var name in allowed)
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["HOME"] = root;
        start.Environment["USERPROFILE"] = root;
        start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GIT_OPTIONAL_LOCKS"] = "0";
    }

    private static void AssertSuppressed(uint mode) => Assert.Equal(
        ProcessTreeGuiSuppression.SuppressedErrorModeFlags, mode & ProcessTreeGuiSuppression.SuppressedErrorModeFlags);

    [DllImport("kernel32.dll")]
    private static extern uint GetErrorMode();
}
