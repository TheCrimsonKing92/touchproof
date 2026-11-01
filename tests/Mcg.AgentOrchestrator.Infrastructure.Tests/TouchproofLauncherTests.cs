using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: each subprocess has its own directory, environment and closed stdin.
public sealed class TouchproofLauncherTests
{
    private const string Usage = "Usage: touchproof <command> [options]";
    private const string Alias = "mcg-orchestrator.cmd still works as an alias.";

    [Fact]
    public void Script_AtRepositoryRoot_ContainsExactForwarder()
    {
        var path = Path.Combine(VerifiedRepositoryRoot.Find(), "touchproof.cmd");
        Assert.True(File.Exists(path), path);
        Assert.Equal(
            ["@echo off", "call \"%~dp0mcg-orchestrator.cmd\" %*", "exit /b %errorlevel%"],
            File.ReadAllLines(path));
    }

    [Fact]
    public void Forwarder_QuotedArguments_PreservesArgumentsAndFailureExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new LauncherSandbox();
        sandbox.WriteInnerLauncher("@echo off\r\necho %*\r\nexit /b 7\r\n");

        const string arguments = "--help \"argument with spaces\" --flag value";
        var original = sandbox.RunLauncher("mcg-orchestrator.cmd", arguments);
        var forwarded = sandbox.RunLauncher("touchproof.cmd", arguments);

        Assert.Equal(7, original.ExitCode);
        Assert.Equal(arguments, original.StandardOutput.TrimEnd('\r', '\n'));
        Assert.Equal(original, forwarded);
        Assert.Empty(forwarded.StandardError);
    }

    [Fact]
    public void Forwarder_Help_MatchesInnerLauncherUsageAndAlias()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var sandbox = new LauncherSandbox();
        sandbox.WriteInnerLauncher(
            $"@echo off\r\ndotnet exec \"{AppAssemblyPath}\" %*\r\nexit /b %errorlevel%\r\n");

        var original = sandbox.RunLauncher("mcg-orchestrator.cmd", "--help");
        var forwarded = sandbox.RunLauncher("touchproof.cmd", "--help");

        Assert.Equal(0, original.ExitCode);
        Assert.Equal(original, forwarded);
        Assert.Empty(forwarded.StandardError);
        Assert.Contains(Usage + Environment.NewLine + Alias, forwarded.StandardOutput,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("mcg-orchestrator.cmd")]
    [InlineData("touchproof.cmd")]
    public void RuntimePath_EitherLauncher_RequiresRelaunch(string path)
    {
        var decision = RepositoryChangeClassifier.DecideConductorRelaunch([path]);
        Assert.True(decision.Required);
        Assert.Equal("launcher-entry", decision.Classification);
    }

    [Fact]
    public void Startup_NoArguments_PrintsTouchproofBanner()
    {
        using var sandbox = new LauncherSandbox();
        var git = sandbox.Run("git", "init");
        Assert.True(git.ExitCode == 0, git.StandardError);

        var result = sandbox.Run("dotnet", "exec", AppAssemblyPath);

        Assert.True(result.ExitCode == 0, result.StandardError);
        Assert.Equal("Touchproof", new StringReader(result.StandardOutput).ReadLine());
        Assert.DoesNotContain("MCG Agent Orchestrator", result.StandardOutput,
            StringComparison.Ordinal);
    }

    private static string AppAssemblyPath =>
        Path.Combine(AppContext.BaseDirectory, "Mcg.AgentOrchestrator.App.dll");

    private sealed class LauncherSandbox : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(),
            $"touchproof launcher {Guid.NewGuid():N}");
        private readonly string _launchers;

        internal LauncherSandbox()
        {
            _launchers = Path.Combine(_root, "launchers");
            Directory.CreateDirectory(_launchers);
            Directory.CreateDirectory(Path.Combine(_root, "home"));
            File.Copy(Path.Combine(VerifiedRepositoryRoot.Find(), "touchproof.cmd"),
                Path.Combine(_launchers, "touchproof.cmd"));
        }

        internal void WriteInnerLauncher(string text) =>
            File.WriteAllText(Path.Combine(_launchers, "mcg-orchestrator.cmd"), text);

        internal CliChildProcessResult RunLauncher(string name, string arguments)
        {
            var info = CreateStartInfo("cmd.exe");
            // cmd /s /c removes the outer quotes; the script path retains its own pair.
            info.Arguments = $"/d /s /c \"\"{Path.Combine(_launchers, name)}\" {arguments}\"";
            return CliChildProcessRunner.Run(info);
        }

        internal CliChildProcessResult Run(string executable, params string[] arguments)
        {
            var info = CreateStartInfo(executable);
            foreach (var argument in arguments) info.ArgumentList.Add(argument);
            return CliChildProcessRunner.Run(info);
        }

        private ProcessStartInfo CreateStartInfo(string executable)
        {
            var info = new ProcessStartInfo(executable)
            {
                WorkingDirectory = _root,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            info.Environment.Clear();
            foreach (var key in new[]
            {
                "PATH", "SystemRoot", "WINDIR", "COMSPEC", "PATHEXT", "DOTNET_ROOT",
                "ProgramFiles", "ProgramFiles(x86)"
            })
            {
                if (Environment.GetEnvironmentVariable(key) is { } value)
                    info.Environment[key] = value;
            }
            var home = Path.Combine(_root, "home");
            foreach (var key in new[] { "HOME", "USERPROFILE", "LOCALAPPDATA", "APPDATA",
                "DOTNET_CLI_HOME", "TEMP", "TMP" })
                info.Environment[key] = home;
            info.Environment[VerifiedRepositoryRoot.VariableName] = _root;
            info.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] =
                Path.Combine(_root, "dotnet");
            info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            info.Environment["DOTNET_NOLOGO"] = "1";
            info.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            info.Environment["GIT_CONFIG_GLOBAL"] = Path.Combine(_root, "empty-gitconfig");
            return info;
        }

        public void Dispose() => Directory.Delete(_root, recursive: true);
    }
}
