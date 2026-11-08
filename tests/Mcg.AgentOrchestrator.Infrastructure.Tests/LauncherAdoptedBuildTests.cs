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
