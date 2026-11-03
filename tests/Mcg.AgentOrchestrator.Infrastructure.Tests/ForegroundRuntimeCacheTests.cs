using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

// Parallel-safe: each case owns its copied production scripts, synthetic closure,
// git/build/native seams, environment, ACLs, junctions, and caller directory.
// All child execution and bounded cleanup go through TestChildProcessCapture.
public sealed class ForegroundRuntimeCacheTests
{
    [Xunit.Fact]
    public void Helper_cold_and_warm_calls_use_verified_repository_cache()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        var first = fixture.Helper("goals", "--name", "two words");
        Assert.Equal(0, first.ExitCode);
        var argv = fixture.NativeArguments();
        var run = Path.GetDirectoryName(argv[0])!;
        Assert.Equal(Path.Combine(fixture.CacheRoot, "v2"), Path.GetDirectoryName(run));
        Assert.Equal(new[] { argv[0], "goals", "--name", "two words" }, argv);
        var seal = File.ReadAllText(Path.Combine(run, ".mcg-run-closure.json"));
        Assert.Equal("app-v1", File.ReadAllText(fixture.ObservedApp));
        Assert.Equal("dependency-v1", File.ReadAllText(Path.Combine(run, "dependency.dll")));
        Assert.True(File.Exists(Path.Combine(run, "runtimes", "win-x64", "native", "e_sqlite3.dll")));
        Assert.False(Directory.Exists(Path.Combine(fixture.TempRoot, "mcg-run")));
        Assert.False(File.Exists(fixture.BuildLog));

        var warm = fixture.Helper("goals", "--name", "two words");
        Assert.Equal(0, warm.ExitCode);
        Assert.Equal(argv, fixture.NativeArguments());
        Assert.Equal(seal, File.ReadAllText(Path.Combine(run, ".mcg-run-closure.json")));
        Assert.Equal(new[] { fixture.TempRoot, fixture.TempRoot }, File.ReadAllLines(fixture.NativeEnvironment));
        Assert.Equal(ReadEnvironment(fixture.BeforeEnvironment), ReadEnvironment(fixture.AfterEnvironment));
    }

    [Xunit.Fact]
    public void Helper_preserves_option_looking_arguments_after_the_native_command()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        string[] tail = ["goals", "--name", "two words", "--runtime-cache-root", "native value", "--limit", "5"];
        Assert.Equal(0, fixture.Helper(tail).ExitCode);
        Assert.Equal(tail, fixture.NativeArguments().Skip(1));
        Assert.False(File.Exists(fixture.BuildLog));
    }

    [Xunit.Fact]
    public void Helper_source_change_rebuilds_before_selecting_a_new_digest()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        Assert.Equal(0, fixture.Helper("goals").ExitCode);
        var original = fixture.NativeArguments()[0];
        File.WriteAllText(fixture.SourceFile, "// changed source");
        File.SetLastWriteTimeUtc(fixture.SourceFile, new DateTime(2045, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var rebuilt = fixture.Helper("goals");
        Assert.Equal(0, rebuilt.ExitCode);
        Assert.Equal(new[] { "build" }, File.ReadAllLines(fixture.BuildLog));
        Assert.Equal(CacheFixture.Head, File.ReadAllText(fixture.AppDll + ".git-head").Trim());
        Assert.NotEqual(original, fixture.NativeArguments()[0]);
        Assert.Equal("app-rebuilt", File.ReadAllText(fixture.ObservedApp));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(fixture.NativeArguments()[0])!, ".mcg-run-closure.json")));
    }

    [Xunit.Fact]
    public void Helper_dependency_change_selects_new_verified_content_without_build()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        Assert.Equal(0, fixture.Helper("goals").ExitCode);
        var original = fixture.NativeArguments()[0];
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.AppDll)!, "dependency.dll"), "dependency-v2");

        Assert.Equal(0, fixture.Helper("goals").ExitCode);
        var selected = fixture.NativeArguments()[0];
        Assert.NotEqual(original, selected);
        Assert.Equal("dependency-v2", File.ReadAllText(Path.Combine(Path.GetDirectoryName(selected)!, "dependency.dll")));
        Assert.False(File.Exists(fixture.BuildLog));
    }

    [Xunit.Fact]
    public void Helper_repairs_missing_content_but_refuses_a_conflicting_closure()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        Assert.Equal(0, fixture.Helper("goals").ExitCode);
        var original = fixture.NativeArguments()[0];
        var dependency = Path.Combine(Path.GetDirectoryName(original)!, "dependency.dll");
        File.Delete(dependency);
        Assert.Equal(0, fixture.Helper("goals").ExitCode);
        Assert.Equal(original, fixture.NativeArguments()[0]);
        Assert.Equal("dependency-v1", File.ReadAllText(dependency));

        File.WriteAllText(dependency, "conflict");
        File.Delete(fixture.ArgumentsLog);
        var refused = fixture.Helper("goals");
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("invalid and will not be modified or launched", refused.Stderr);
        Assert.Contains(Path.GetDirectoryName(original)!, refused.Stderr);
        Assert.False(File.Exists(fixture.ArgumentsLog));
        Assert.Equal("conflict", File.ReadAllText(dependency));
    }

    [Xunit.Fact]
    public void Explicit_launcher_prefix_preserves_the_exact_native_tail_and_exit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        var externalCache = Path.Combine(fixture.Root, "external cache");
        string[] tail = ["goals", "--name", "two words", "", "left&right", "bang!literal",
            "--runtime-cache-root", "native option value", "brackets (x)"];
        var result = fixture.Launcher(["--runtime-cache-root", externalCache, .. tail], exitCode: 7);
        Assert.Equal(7, result.ExitCode);
        var argv = fixture.NativeArguments();
        Assert.Equal(tail, argv.Skip(1));
        Assert.Equal(Path.Combine(externalCache, "v2"), Path.GetDirectoryName(Path.GetDirectoryName(argv[0])));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(argv[0])!, ".mcg-run-closure.json")));
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Xunit.Fact]
    public void Omitted_launcher_option_keeps_the_default_cache()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        Assert.Equal(0, fixture.Launcher(["goals", "--name", "two words"]).ExitCode);
        var argv = fixture.NativeArguments();
        Assert.Equal(new[] { argv[0], "goals", "--name", "two words" }, argv);
        Assert.Equal(Path.Combine(fixture.TempRoot, "mcg-run", "v2"),
            Path.GetDirectoryName(Path.GetDirectoryName(argv[0])));
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Xunit.Fact]
    public void Helper_explicit_selection_cannot_execute_an_eligible_adopted_pointer()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        var adoptedDll = Path.Combine(fixture.Repository, "adopted", "Mcg.AgentOrchestrator.App.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(adoptedDll)!);
        File.WriteAllText(adoptedDll, "unchecked-adopted");
        var pointer = Path.Combine(fixture.Repository, ".orchestrator", "continuity", "home-build-adoption.cmd.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(pointer)!);
        File.WriteAllLines(pointer, [CacheFixture.Head, adoptedDll]);
        // Positive control proves the pointer is eligible before testing explicit selection.
        Assert.Equal(0, fixture.Launcher(["goals"], intree: false).ExitCode);
        Assert.Equal(adoptedDll, fixture.NativeArguments()[0]);
        Assert.Equal(0, fixture.Helper("goals").ExitCode);
        Assert.StartsWith(fixture.CacheRoot + Path.DirectorySeparatorChar, fixture.NativeArguments()[0]);
        Assert.Equal("app-v1", File.ReadAllText(fixture.ObservedApp));
    }

    [Xunit.Theory]
    [Xunit.InlineData("missing")]
    [Xunit.InlineData("empty")]
    [Xunit.InlineData("duplicate")]
    [Xunit.InlineData("relative")]
    [Xunit.InlineData("drive-relative")]
    [Xunit.InlineData("drive-unspecified")]
    [Xunit.InlineData("malformed")]
    public void Invalid_prefix_fails_before_build_publication_or_native_execution(string kind)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        File.Delete(fixture.AppDll); // A bad prefix must be refused even when a build is needed.
        string[] arguments = kind switch
        {
            "missing" => ["--runtime-cache-root"],
            "empty" => ["--runtime-cache-root", "", "goals"],
            "duplicate" => ["--runtime-cache-root", fixture.CacheRoot, "--runtime-cache-root", fixture.CacheRoot, "goals"],
            "relative" => ["--runtime-cache-root", "relative cache", "goals"],
            "drive-relative" => ["--runtime-cache-root", "C:cache", "goals"],
            "drive-unspecified" => ["--runtime-cache-root", @"\cache", "goals"],
            _ => ["--runtime-cache-root", @"C:\bad|cache", "goals"]
        };
        var result = fixture.Launcher(arguments);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("cache", result.Stderr, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.ArgumentsLog));
        Assert.False(File.Exists(fixture.BuildLog));
        Assert.False(Directory.Exists(fixture.CacheRoot));
        Assert.False(Directory.Exists(Path.Combine(fixture.Repository, ".build-lock")));
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Helper_refuses_escaping_junctions_at_either_cache_ancestor(bool leaf)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        // Sibling prefix deliberately defeats a naive lexical StartsWith(repo) check.
        var outside = fixture.Repository + " escape";
        Directory.CreateDirectory(outside);
        var link = leaf ? fixture.CacheRoot : Path.Combine(fixture.Repository, ".orchestrator");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        fixture.CreateJunction(link, outside);
        try
        {
            var result = fixture.Helper("goals");
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("escapes repository", result.Stderr);
            Assert.False(File.Exists(fixture.ArgumentsLog));
            Assert.False(File.Exists(fixture.BuildLog));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
        }
        finally { Directory.Delete(link); }
    }

    [Xunit.Fact]
    public void Helper_allows_a_contained_junction_and_a_nonexistent_cache_suffix()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        var target = Path.Combine(fixture.Repository, "contained state");
        Directory.CreateDirectory(target);
        var link = Path.Combine(fixture.Repository, ".orchestrator");
        fixture.CreateJunction(link, target);
        try
        {
            Assert.Equal(0, fixture.Helper("goals").ExitCode);
            Assert.True(Directory.Exists(Path.Combine(target, "operator-run-cache", "v2")));
            Assert.StartsWith(fixture.CacheRoot + Path.DirectorySeparatorChar, fixture.NativeArguments()[0]);
        }
        finally { Directory.Delete(link); }
    }

    [Xunit.Theory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public void Helper_refuses_a_file_at_either_cache_ancestor(bool leaf)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        var file = leaf ? fixture.CacheRoot : Path.Combine(fixture.Repository, ".orchestrator");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "owned file");
        var result = fixture.Helper("goals");
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("not a directory", result.Stderr);
        Assert.False(File.Exists(fixture.ArgumentsLog));
        Assert.False(File.Exists(fixture.BuildLog));
        Assert.Equal("owned file", File.ReadAllText(file));
    }

    [Xunit.Fact]
    public void Selected_root_write_denial_fails_without_native_execution_or_fallback()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        Directory.CreateDirectory(fixture.CacheRoot);
        var directory = new DirectoryInfo(fixture.CacheRoot);
        var original = directory.GetAccessControl();
        var denied = directory.GetAccessControl();
        var identity = WindowsIdentity.GetCurrent().User!;
        denied.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Deny));
        directory.SetAccessControl(denied);
        try
        {
            // Verify the intended OS boundary; fixture setup failure cannot count as refusal.
            Assert.Throws<UnauthorizedAccessException>(() => Directory.CreateDirectory(Path.Combine(fixture.CacheRoot, "probe")));
            var result = fixture.Helper("goals");
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("operator-run-cache", result.Stderr);
            Assert.False(File.Exists(fixture.ArgumentsLog));
            Assert.False(File.Exists(fixture.BuildLog));
            Assert.False(Directory.Exists(Path.Combine(fixture.TempRoot, "mcg-run")));
        }
        finally { directory.SetAccessControl(original); }
    }

    [Xunit.Fact]
    public void Workspace_binding_requires_prebuilt_dll_and_preserves_explicit_arguments()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        var state = Path.Combine(fixture.Root, "bound state");
        var refused = fixture.BoundHelper("-RepositoryRoot", fixture.Root, "-ExpectedStateDirectory", state, "goals");
        Assert.NotEqual(0, refused.ExitCode);
        Assert.Contains("explicit prebuilt -AppDll", refused.Stderr);
        Assert.False(Directory.Exists(fixture.CacheRoot));
        Assert.False(File.Exists(fixture.ArgumentsLog));

        var explicitDll = fixture.BoundHelper("-AppDll", fixture.AppDll, "-RepositoryRoot", fixture.Root,
            "-ExpectedStateDirectory", state, "-DisposableFixture", "goals");
        Assert.Equal(0, explicitDll.ExitCode);
        Assert.Equal(new[] { fixture.AppDll, "goals", "--repository-root", fixture.Root,
            "--expected-state-directory", state, "--disposable-fixture" }, fixture.NativeArguments());
        Assert.False(Directory.Exists(fixture.CacheRoot));
        Assert.False(File.Exists(fixture.BuildLog));
    }

    private static KeyValuePair<string, string>[] ReadEnvironment(string path) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!
            .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).ToArray();

    private sealed class CacheFixture : IDisposable
    {
        internal const string Head = "controlled-fixture-head";
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"foreground cache {Guid.NewGuid():N}");
        internal string Repository => Path.Combine(Root, "repo with spaces");
        internal string CacheRoot => Path.Combine(Repository, ".orchestrator", "operator-run-cache");
        internal string TempRoot => Path.Combine(Root, "caller temp");
        private string Caller => Path.Combine(Root, "caller elsewhere");
        private string ShimDirectory => Path.Combine(Root, "shim");
        private string NativeHost => Path.Combine(ShimDirectory, "native-host.exe");
        internal string SourceFile => Path.Combine(Repository, "src", "Mcg.AgentOrchestrator.App", "Program.cs");
        internal string AppDll => Path.Combine(Path.GetDirectoryName(SourceFile)!, "bin", "Debug", "net10.0", "Mcg.AgentOrchestrator.App.dll");
        internal string ArgumentsLog => Path.Combine(Root, "argv.base64");
        internal string ObservedApp => Path.Combine(Root, "observed-app.txt");
        internal string NativeEnvironment => Path.Combine(Root, "native-env.txt");
        internal string BeforeEnvironment => Path.Combine(Root, "before-env.json");
        internal string AfterEnvironment => Path.Combine(Root, "after-env.json");
        internal string BuildLog => Path.Combine(Root, "build.log");
        private string PowerShell => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");

        internal CacheFixture()
        {
            foreach (var directory in new[] { Repository, TempRoot, Caller, ShimDirectory,
                         Path.Combine(Repository, "scripts"), Path.GetDirectoryName(AppDll)! })
                Directory.CreateDirectory(directory);
            var sourceRoot = LauncherScriptTests.FindLauncherSourceRoot();
            File.Copy(Path.Combine(sourceRoot, "mcg-orchestrator.cmd"), Path.Combine(Repository, "mcg-orchestrator.cmd"));
            foreach (var script in new[] { "Invoke-OrchestratorCommand.ps1", "resolve-run-dir.ps1",
                         "Test-OrchestratorArtifactFreshness.ps1", "Update-AppDllGitHeadMarker.ps1" })
                File.Copy(Path.Combine(sourceRoot, "scripts", script), Path.Combine(Repository, "scripts", script));
            foreach (var file in new[] { SourceFile, Path.Combine(Path.GetDirectoryName(SourceFile)!, "Mcg.AgentOrchestrator.App.csproj"),
                         Path.Combine(Repository, "Directory.Build.props"), Path.Combine(Repository, "Directory.Build.rsp"),
                         Path.Combine(Repository, "global.json") })
            {
                File.WriteAllText(file, "// controlled input");
                File.SetLastWriteTimeUtc(file, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            }
            foreach (var project in new[] { "App", "Core", "Infrastructure", "Infrastructure.Providers", "Infrastructure.OperatorComms" })
            {
                var obj = Path.Combine(Repository, "src", "Mcg.AgentOrchestrator." + project, "obj");
                Directory.CreateDirectory(obj);
                File.WriteAllText(Path.Combine(obj, "project.assets.json"), "{}");
            }
            File.WriteAllText(AppDll, "app-v1");
            File.SetLastWriteTimeUtc(AppDll, new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc));
            File.WriteAllText(AppDll + ".git-head", Head);
            var output = Path.GetDirectoryName(AppDll)!;
            File.WriteAllText(Path.Combine(output, "dependency.dll"), "dependency-v1");
            var native = Path.Combine(output, "runtimes", "win-x64", "native");
            Directory.CreateDirectory(native);
            File.WriteAllText(Path.Combine(native, "e_sqlite3.dll"), "sqlite fixture");
            File.WriteAllText(Path.Combine(ShimDirectory, "git.cmd"), $"""
                @echo off
                if "%~3"=="rev-parse" (
                    echo {Head}
                    exit /b 0
                )
                if "%~3"=="diff" exit /b 0
                exit /b 91
                """.ReplaceLineEndings("\r\n"));

            // A real executable records the native argv, avoiding a second batch or
            // PowerShell argument parser that could hide forwarding defects.
            var compileScript = Path.Combine(Root, "compile-native-host.ps1");
            File.WriteAllText(compileScript, """
                Add-Type -OutputType ConsoleApplication -OutputAssembly $env:FIXTURE_NATIVE_HOST -TypeDefinition @'
                using System;
                using System.IO;
                using System.Text;
                public static class NativeHost {
                    public static int Main(string[] args) {
                        string app = Environment.GetEnvironmentVariable("FIXTURE_APP_DLL");
                        if (args.Length > 0 && args[0] == "build") {
                            File.AppendAllText(Environment.GetEnvironmentVariable("FIXTURE_BUILD_LOG"), "build\n");
                            File.WriteAllText(app, "app-rebuilt");
                            File.SetLastWriteTimeUtc(app, new DateTime(2050, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                            return 0;
                        }
                        var encoded = new string[args.Length];
                        for (int i = 0; i < args.Length; i++)
                            encoded[i] = Convert.ToBase64String(Encoding.UTF8.GetBytes(args[i]));
                        File.WriteAllLines(Environment.GetEnvironmentVariable("FIXTURE_ARGV_LOG"), encoded);
                        File.WriteAllText(Environment.GetEnvironmentVariable("FIXTURE_OBSERVED_APP"), File.ReadAllText(args[0]));
                        File.WriteAllLines(Environment.GetEnvironmentVariable("FIXTURE_NATIVE_ENV"),
                            new string[] { Environment.GetEnvironmentVariable("TEMP"), Environment.GetEnvironmentVariable("TMP") });
                        return int.Parse(Environment.GetEnvironmentVariable("FIXTURE_EXIT"));
                    }
                }
                '@
                """);
            var compile = RunPowerShell(compileScript, []);
            Assert.Equal(0, compile.ExitCode);
            Assert.True(File.Exists(NativeHost), compile.Stderr);
            File.WriteAllText(Path.Combine(Root, "invoke-helper.ps1"), $"""
                param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
                $ErrorActionPreference = 'Stop'
                [IO.File]::WriteAllText('{Ps(BeforeEnvironment)}', ([Environment]::GetEnvironmentVariables('Process') | ConvertTo-Json -Compress))
                & '{Ps(Path.Combine(Repository, "scripts", "Invoke-OrchestratorCommand.ps1"))}' @Arguments
                [IO.File]::WriteAllText('{Ps(AfterEnvironment)}', ([Environment]::GetEnvironmentVariables('Process') | ConvertTo-Json -Compress))
                """);
        }

        internal string[] NativeArguments()
        {
            Assert.True(File.Exists(ArgumentsLog), "Native executable did not record argv.");
            return File.ReadAllLines(ArgumentsLog).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
        }

        internal TestChildProcessCapture.Result Helper(params string[] arguments) =>
            RunPowerShell(Path.Combine(Root, "invoke-helper.ps1"), arguments);

        internal TestChildProcessCapture.Result BoundHelper(params string[] arguments) =>
            RunPowerShell(Path.Combine(Repository, "scripts", "Invoke-OrchestratorCommand.ps1"), arguments);

        private TestChildProcessCapture.Result RunPowerShell(string script, string[] arguments)
        {
            var start = StartInfo(PowerShell);
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
                start.ArgumentList.Add(argument);
            return TestChildProcessCapture.Run(start, TimeSpan.FromMinutes(3));
        }

        internal TestChildProcessCapture.Result Launcher(string[] arguments, int exitCode = 0, bool intree = true)
        {
            var start = StartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
            start.Environment["FIXTURE_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["MCG_ORCHESTRATOR_LAUNCHER_INTREE"] = intree ? "1" : "0";
            start.Arguments = "/d /s /c \"" + string.Join(" ",
                new[] { Path.Combine(Repository, "mcg-orchestrator.cmd") }.Concat(arguments).Select(value => "\"" + value + "\"")) + "\"";
            return TestChildProcessCapture.Run(start, TimeSpan.FromMinutes(3));
        }

        internal void CreateJunction(string link, string target)
        {
            var start = StartInfo(PowerShell);
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add($"New-Item -ItemType Junction -Path '{Ps(link)}' -Target '{Ps(target)}' -ErrorAction Stop | Out-Null");
            var result = TestChildProcessCapture.Run(start);
            Assert.Equal(0, result.ExitCode);
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "Junction fixture was not created.");
        }

        private ProcessStartInfo StartInfo(string executable)
        {
            var start = new ProcessStartInfo(executable)
            {
                WorkingDirectory = Caller, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "WINDIR", "COMSPEC", "USERPROFILE", "LOCALAPPDATA", "APPDATA" })
                start.Environment[name] = Environment.GetEnvironmentVariable(name);
            start.Environment["OS"] = "Windows_NT";
            start.Environment["PATH"] = ShimDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
            start.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
            start.Environment["TEMP"] = TempRoot;
            start.Environment["TMP"] = TempRoot;
            start.Environment["MCG_ORCHESTRATOR_DOTNET_PATH"] = NativeHost;
            // Leave adoption eligible for helper calls; the explicit option must bypass it.
            start.Environment["MCG_ORCHESTRATOR_LAUNCHER_INTREE"] = "0";
            start.Environment["FIXTURE_NATIVE_HOST"] = NativeHost;
            start.Environment["FIXTURE_APP_DLL"] = AppDll;
            start.Environment["FIXTURE_ARGV_LOG"] = ArgumentsLog;
            start.Environment["FIXTURE_BUILD_LOG"] = BuildLog;
            start.Environment["FIXTURE_OBSERVED_APP"] = ObservedApp;
            start.Environment["FIXTURE_NATIVE_ENV"] = NativeEnvironment;
            start.Environment["FIXTURE_EXIT"] = "0";
            return start;
        }

        private static string Ps(string value) => value.Replace("'", "''", StringComparison.Ordinal);
        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
