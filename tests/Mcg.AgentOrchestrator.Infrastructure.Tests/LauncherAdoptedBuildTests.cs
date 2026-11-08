using System.Diagnostics;
using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each real-process contract test owns its repository, shims, logs, and TEMP/TMP.
// The production acceptance process host owns launch, concurrent output drains, and bounded cleanup.
public sealed class LauncherAdoptedBuildTests
{
    [Xunit.Fact]
    public async Task MatchingCleanPointer_RunsAdoptedDll_PreservingArgumentsAndExitCode()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new LauncherFixture();

        var result = await fixture.Run(exitCode: 7, trapPowerShell: true);

        Assert.False(result.TimedOut, $"Launcher did not exit: {result.Output}");
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("ADOPTED-STUB-MARKER", result.Output);
        Assert.Equal(new[] { fixture.AdoptedDll, "goals", "--name", "two words", "--limit", "5" },
            File.ReadAllLines(fixture.ArgumentsLog));
        Assert.Single(File.ReadAllLines(fixture.DotnetLog));
        Assert.False(File.Exists(fixture.PowerShellLog));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".build-lock")));
        Assert.Equal(new[] { fixture.Root + "\\", "7", fixture.CallerDirectory },
            File.ReadAllLines(fixture.EnvironmentLog));
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing-dll")]
    [Xunit.InlineData("escape-hatch")]
    [Xunit.InlineData("missing-pointer")]
    [Xunit.InlineData("one-line-pointer")]
    [Xunit.InlineData("empty-commit")]
    [Xunit.InlineData("empty-dll")]
    public async Task IneligiblePointer_UsesExistingFreshnessBuildAndRunPath(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new LauncherFixture();
        if (scenario == "missing-dll") File.Delete(fixture.AdoptedDll);
        if (scenario == "missing-pointer") File.Delete(fixture.Pointer);
        if (scenario == "one-line-pointer") File.WriteAllText(fixture.Pointer, fixture.Head + "\r\n");
        if (scenario == "empty-commit") fixture.WritePointer("", fixture.AdoptedDll);
        if (scenario == "empty-dll") fixture.WritePointer(fixture.Head, "");

        var result = await fixture.Run(intree: scenario == "escape-hatch");

        Assert.False(result.TimedOut, $"Launcher did not exit: {result.Output}");
        Assert.True(result.ExitCode == 0, $"exit={result.ExitCode}: {result.Output}");
        Assert.DoesNotContain("ADOPTED-STUB-MARKER", result.Output);
        var calls = File.ReadAllLines(fixture.DotnetLog);
        Assert.Equal(2, calls.Length);
        Assert.StartsWith("build ", calls[0]);
        Assert.Contains("--no-restore", calls[0]);
        Assert.Contains("mcg-run", calls[1]);
        Assert.Equal(fixture.Head, File.ReadAllText(fixture.AppDll + ".git-head").Trim());
    }

    [Xunit.Theory]
    [Xunit.InlineData("different-commit")]
    [Xunit.InlineData("dirty-source")]
    [Xunit.InlineData("Directory.Build.props")]
    [Xunit.InlineData("Directory.Build.rsp")]
    [Xunit.InlineData("global.json")]
    [Xunit.InlineData("no-git")]
    public async Task EligiblePointer_RunsAdoptedDll_WhateverCheckoutState(string scenario)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new LauncherFixture();
        if (scenario == "different-commit") fixture.WritePointer(new string('f', 40), fixture.AdoptedDll);
        else if (scenario == "dirty-source") File.AppendAllText(fixture.SourceFile, "// local change\n");
        else if (scenario == "no-git") SharedTestSupport.RemoveTempDirectory(Path.Combine(fixture.Root, ".git"));
        else File.AppendAllText(Path.Combine(fixture.Root, scenario), "\n");

        var result = await fixture.Run(exitCode: 7, trapPowerShell: true);

        Assert.False(result.TimedOut, $"Launcher did not exit: {result.Output}");
        Assert.Equal(7, result.ExitCode);
        Assert.Contains("ADOPTED-STUB-MARKER", result.Output);
        Assert.Equal(new[] { fixture.AdoptedDll, "goals", "--name", "two words", "--limit", "5" },
            File.ReadAllLines(fixture.ArgumentsLog));
        var call = Assert.Single(File.ReadAllLines(fixture.DotnetLog));
        Assert.DoesNotContain("build ", call);
        Assert.False(File.Exists(fixture.PowerShellLog));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, ".build-lock")));
    }

    [Xunit.Theory]
    [Xunit.InlineData("adopted")]
    [Xunit.InlineData("escape-hatch")]
    [Xunit.InlineData("runtime-cache-root")]
    public void LauncherRewrittenWhileAppRuns_ExitsWithAppCodeWithoutReadingReplacement(string route)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new LauncherFixture();

        var result = fixture.RunRewritingLauncher(route);

        Assert.Equal(37, result.ExitCode);
        foreach (var output in new[] { result.Stdout, result.Stderr })
        {
            Assert.DoesNotContain("is not recognized", output, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("REWRITTEN-LAUNCHER-MARKER", output);
        }
        Assert.Equal(File.ReadAllBytes(fixture.Replacement), File.ReadAllBytes(fixture.Launcher));
        var arguments = File.ReadAllLines(fixture.ArgumentsLog);
        Assert.Equal(new[] { "goals", "--name", "two words", "--limit", "5" }, arguments.Skip(1));
        if (route == "adopted")
        {
            Assert.Equal(fixture.AdoptedDll, arguments[0]);
            Assert.Equal(new[] { "app" }, File.ReadAllLines(fixture.DotnetLog));
        }
        else
        {
            var cache = route == "runtime-cache-root" ? fixture.SelectedCache : Path.Combine(fixture.Root, "mcg-run");
            Assert.StartsWith(Path.Combine(cache, "v2") + Path.DirectorySeparatorChar, arguments[0]);
            Assert.Equal("Mcg.AgentOrchestrator.App.dll", Path.GetFileName(arguments[0]));
            Assert.Equal(new[] { "build", "app" }, File.ReadAllLines(fixture.DotnetLog));
        }
    }

    private sealed class LauncherFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"adopted launcher {Guid.NewGuid():N}");
        internal string CallerDirectory => Path.Combine(Root, "caller");
        private string ShimDirectory => Path.Combine(Root, "shim");
        internal string SourceFile => Path.Combine(Root, "src", "Mcg.AgentOrchestrator.App", "Program.cs");
        internal string AppDll => Path.Combine(Root, "src", "Mcg.AgentOrchestrator.App", "bin", "Debug", "net10.0", "Mcg.AgentOrchestrator.App.dll");
        internal string AdoptedDll => Path.Combine(Root, "adopted run", "Mcg.AgentOrchestrator.App.dll");
        internal string Pointer => Path.Combine(Root, ".orchestrator", "continuity", "home-build-adoption.cmd.txt");
        internal string DotnetLog => Path.Combine(Root, "dotnet.log");
        internal string ArgumentsLog => Path.Combine(Root, "arguments.log");
        internal string EnvironmentLog => Path.Combine(Root, "environment.log");
        internal string PowerShellLog => Path.Combine(Root, "powershell.log");
        internal string Head { get; }

        internal LauncherFixture()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SourceFile)!);
            Directory.CreateDirectory(Path.GetDirectoryName(AppDll)!);
            Directory.CreateDirectory(Path.GetDirectoryName(AdoptedDll)!);
            Directory.CreateDirectory(Path.GetDirectoryName(Pointer)!);
            Directory.CreateDirectory(CallerDirectory);
            Directory.CreateDirectory(ShimDirectory);
            Directory.CreateDirectory(Path.Combine(Root, "scripts"));
            var sourceRoot = LauncherScriptTests.FindLauncherSourceRoot();
            File.Copy(Path.Combine(sourceRoot, "mcg-orchestrator.cmd"), Path.Combine(Root, "mcg-orchestrator.cmd"));
            foreach (var script in new[] { "Test-OrchestratorArtifactFreshness.ps1", "resolve-run-dir.ps1", "Update-AppDllGitHeadMarker.ps1" })
                File.Copy(Path.Combine(sourceRoot, "scripts", script), Path.Combine(Root, "scripts", script));
            File.WriteAllText(SourceFile, "Console.WriteLine(\"fixture\");\n");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(SourceFile)!, "Mcg.AgentOrchestrator.App.csproj"), "<Project />");
            File.WriteAllText(Path.Combine(Root, "Directory.Build.props"), "<Project />");
            File.WriteAllText(Path.Combine(Root, "Directory.Build.rsp"), "");
            File.WriteAllText(Path.Combine(Root, "global.json"), "{}");
            File.WriteAllText(AppDll, "INTREE-STUB-MARKER");
            File.WriteAllText(AppDll + ".git-head", "stale-head");
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(AppDll)!, "e_sqlite3.dll"), "native");
            File.WriteAllText(AdoptedDll, "ADOPTED-STUB-MARKER");
            foreach (var project in new[] { "App", "Core", "Infrastructure", "Infrastructure.Providers", "Infrastructure.OperatorComms" })
            {
                var obj = Path.Combine(Root, "src", "Mcg.AgentOrchestrator." + project, "obj");
                Directory.CreateDirectory(obj);
                File.WriteAllText(Path.Combine(obj, "project.assets.json"), "{}");
            }
            Git("init", "--initial-branch=main");
            Git("add", ".");
            Git("-c", "user.name=Launcher Test", "-c", "user.email=launcher@example.invalid", "commit", "-m", "fixture");
            Head = Git("rev-parse", "HEAD").Trim();
            WritePointer(Head, AdoptedDll);

            // The DLL is a marker payload interpreted by the dotnet shim, like LauncherScriptTests.
            WriteBatch("dotnet.cmd", $"""
                @echo off
                >>"{DotnetLog}" echo %*
                if "%~1"=="build" (
                    echo INTREE-REBUILT>"{AppDll}"
                    exit /b 0
                )
                type "%~1"
                >"{EnvironmentLog}" echo %MCG_ORCHESTRATOR_REPOSITORY_ROOT%
                >>"{EnvironmentLog}" echo %DOTNET_GCConserveMemory%
                cd>>"{EnvironmentLog}"
                :next_arg
                if "%~1"=="" goto app_exit
                >>"{ArgumentsLog}" echo %~1
                shift
                goto next_arg
                :app_exit
                exit /b %FAKE_APP_EXIT%
                """);
        }

        internal void WritePointer(string commit, string dll) => File.WriteAllText(
            Pointer, $"{commit}\r\n{dll}\r\n", new UTF8Encoding(false));

        internal Task<GoalAcceptanceVerifier.CommandResult> Run(int exitCode = 0, bool intree = false, bool trapPowerShell = false)
        {
            if (trapPowerShell)
                WriteBatch("powershell.cmd", $"""
                    @echo off
                    echo invoked>"{PowerShellLog}"
                    exit /b 99
                    """);
            // The host constructs its hermetic allow-list; these are explicit per-child inputs.
            var environment = new Dictionary<string, string?>
            {
                ["PATH"] = ShimDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"),
                ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
                ["TEMP"] = Root,
                ["TMP"] = Root,
                ["MCG_ORCHESTRATOR_DOTNET_PATH"] = Path.Combine(ShimDirectory, "dotnet.cmd"),
                ["MCG_ORCHESTRATOR_LAUNCHER_INTREE"] = intree ? "1" : "0",
                ["FAKE_APP_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["ADOPTED_COMMIT"] = "inherited-commit",
                ["ADOPTED_DLL"] = AdoptedDll,
                ["CURRENT_HEAD"] = Head,
                ["GIT_CONFIG_NOSYSTEM"] = "1",
                ["GIT_CONFIG_GLOBAL"] = "NUL",
                ["GIT_TERMINAL_PROMPT"] = "0"
            };
            return AcceptanceProcessTestContext.RunManifestCommandAsync(
                [Path.Combine(Root, "mcg-orchestrator.cmd"), "goals", "--name", "two words", "--limit", "5"],
                CallerDirectory, TimeSpan.FromMinutes(2), environment, resolveBareCommand: false, default);
        }

        internal string Launcher => Path.Combine(Root, "mcg-orchestrator.cmd");
        internal string Replacement => Path.Combine(Root, "replacement.cmd");
        internal string SelectedCache => Path.Combine(Root, "c");
        private string RewriteHost => Path.Combine(ShimDirectory, "rewrite-host.exe");

        internal TestChildProcessCapture.Result RunRewritingLauncher(string route)
        {
            // Repeated lines extend beyond every old resume offset. The broken launcher
            // must execute marker lines (or command fragments), rather than hit EOF quietly.
            const string replacementLine = "echo REWRITTEN-LAUNCHER-MARKER\r\n";
            var repeats = checked((int)(new FileInfo(Launcher).Length * 2 / replacementLine.Length + 2));
            File.WriteAllText(Replacement, string.Concat(Enumerable.Repeat(replacementLine, repeats)), Encoding.ASCII);

            // A batch shim would transfer control away from the launcher and mask the bug.
            // The executable completes the rewrite before returning, with no timing race.
            var compileScript = Path.Combine(Root, "compile-rewrite-host.ps1");
            File.WriteAllText(compileScript, """
                $ErrorActionPreference = 'Stop'
                Add-Type -OutputType ConsoleApplication -OutputAssembly $env:FIXTURE_REWRITE_HOST -TypeDefinition @'
                using System;
                using System.IO;
                public static class RewriteHost {
                    public static int Main(string[] args) {
                        bool build = args.Length > 0 && args[0] == "build";
                        File.AppendAllText(Environment.GetEnvironmentVariable("FIXTURE_DOTNET_LOG"),
                            build ? "build\n" : "app\n");
                        if (build) {
                            File.WriteAllText(Environment.GetEnvironmentVariable("FIXTURE_APP_DLL"), "INTREE-REBUILT");
                            return 0;
                        }
                        File.WriteAllLines(Environment.GetEnvironmentVariable("FIXTURE_ARGUMENTS_LOG"), args);
                        File.Copy(Environment.GetEnvironmentVariable("FIXTURE_REPLACEMENT"),
                            Environment.GetEnvironmentVariable("FIXTURE_LAUNCHER"), true);
                        return 37;
                    }
                }
                '@
                """);
            var powerShell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
                "WindowsPowerShell", "v1.0", "powershell.exe");
            var compile = RewriteStartInfo(powerShell);
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", compileScript })
                compile.ArgumentList.Add(argument);
            var compiled = TestChildProcessCapture.Run(compile, TimeSpan.FromMinutes(3));
            Assert.True(compiled.ExitCode == 0, $"Native rewrite host compilation failed: {compiled}");
            Assert.True(File.Exists(RewriteHost), "Native rewrite host was not compiled.");

            var start = RewriteStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
            start.Environment["MCG_ORCHESTRATOR_LAUNCHER_INTREE"] = route == "escape-hatch" ? "1" : "0";
            var prefix = route == "runtime-cache-root" ? $"--runtime-cache-root \"{SelectedCache}\" " : "";
            start.Arguments = $"/d /s /c \"\"{Launcher}\" {prefix}goals --name \"two words\" --limit 5\"";
            return TestChildProcessCapture.Run(start, TimeSpan.FromMinutes(3));
        }

        private ProcessStartInfo RewriteStartInfo(string executable)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = CallerDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "COMSPEC", "USERPROFILE", "LOCALAPPDATA", "APPDATA" })
                start.Environment[name] = Environment.GetEnvironmentVariable(name);
            start.Environment["OS"] = "Windows_NT";
            start.Environment["PATH"] = ShimDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            start.Environment["TEMP"] = Root;
            start.Environment["TMP"] = Root;
            start.Environment["GIT_CONFIG_NOSYSTEM"] = "1";
            start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
            start.Environment["GIT_TERMINAL_PROMPT"] = "0";
            start.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = RewriteHost;
            start.Environment["FIXTURE_REWRITE_HOST"] = RewriteHost;
            start.Environment["FIXTURE_DOTNET_LOG"] = DotnetLog;
            start.Environment["FIXTURE_APP_DLL"] = AppDll;
            start.Environment["FIXTURE_ARGUMENTS_LOG"] = ArgumentsLog;
            start.Environment["FIXTURE_REPLACEMENT"] = Replacement;
            start.Environment["FIXTURE_LAUNCHER"] = Launcher;
            return start;
        }

        private void WriteBatch(string name, string text) => File.WriteAllText(
            Path.Combine(ShimDirectory, name), text.ReplaceLineEndings("\r\n"), new UTF8Encoding(false));

        private string Git(params string[] arguments)
        {
            var result = RunGitProbe(Root, arguments);
            RequireCompleteGitOutput(result);
            Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result}");
            return result.StandardOutput;
        }

        // Git objects can be read-only on Windows; retain loud failure if cleanup cannot finish.
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
