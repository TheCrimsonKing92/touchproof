using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

// Parallel-safe: each case owns its copied production scripts, synthetic closure,
// git/build/native seams, environment, ACLs, and caller directory.
// All child execution and bounded cleanup go through TestChildProcessCapture.
public sealed class ForegroundRuntimeCacheTests
{
    [Xunit.Fact]
    public void Explicit_launcher_prefix_preserves_the_exact_native_tail_and_exit()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        var externalCache = Path.Combine(fixture.Root, "external cache");
        string[] tail = ["goals", "--name", "two words", "", "left&right", "bang!literal",
            "--runtime-cache-root", "native option value", "brackets (x)"];
        var result = fixture.Launcher(["--runtime-cache-root", externalCache, .. tail], exitCode: 7);
        AssertExitCode(7, result);
        var argv = fixture.NativeArguments();
        Assert.Equal(tail, argv.Skip(1));
        Assert.Equal(Path.Combine(externalCache, "v2"), Path.GetDirectoryName(Path.GetDirectoryName(argv[0])));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(argv[0])!, ".mcg-run-closure.json")));
        Assert.False(Directory.Exists(fixture.CacheRoot));
    }

    [Xunit.Fact]
    public void Explicit_prefix_preserves_unquoted_delimiters_like_the_default_path()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        // Keep these tokens raw: quoting every argument hides CMD's %1/SHIFT
        // splitting of equals signs, commas and semicolons.
        const string rawTail = """
            goals --name=a,b --map=left;right --empty= "" "two words" "left&right" "bang!literal" --runtime-cache-root "native option value" "brackets (x)"
            """;
        string[] expected = ["goals", "--name=a,b", "--map=left;right", "--empty=", "",
            "two words", "left&right", "bang!literal", "--runtime-cache-root", "native option value", "brackets (x)"];
        AssertExitCode(7, fixture.LauncherRaw(rawTail, exitCode: 7));
        var baseline = fixture.NativeArguments();
        Assert.Equal(expected, baseline.Skip(1));
        Assert.Equal(Path.Combine(fixture.TempRoot, "mcg-run", "v2"),
            Path.GetDirectoryName(Path.GetDirectoryName(baseline[0])));

        var externalCache = Path.Combine(fixture.Root, "external cache !literal");
        var prefix = $"--runtime-cache-root \"{externalCache}\"";
        AssertExitCode(7, fixture.LauncherRaw(prefix + " " + rawTail, exitCode: 7));
        var selected = fixture.NativeArguments();
        Assert.Equal(baseline.Skip(1), selected.Skip(1));
        Assert.Equal(Path.Combine(externalCache, "v2"),
            Path.GetDirectoryName(Path.GetDirectoryName(selected[0])));
        Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(selected[0])!, ".mcg-run-closure.json")));
        Assert.False(File.Exists(fixture.BuildLog));
    }

    [Xunit.Fact]
    public void Omitted_launcher_option_keeps_the_default_cache()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new CacheFixture();
        AssertExitCode(0, fixture.Launcher(["goals", "--name", "two words"]));
        var argv = fixture.NativeArguments();
        Assert.Equal(new[] { argv[0], "goals", "--name", "two words" }, argv);
        Assert.Equal(Path.Combine(fixture.TempRoot, "mcg-run", "v2"),
            Path.GetDirectoryName(Path.GetDirectoryName(argv[0])));
        Assert.False(Directory.Exists(fixture.CacheRoot));
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
            var result = fixture.Launcher(["--runtime-cache-root", fixture.CacheRoot, "goals"]);
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("Cannot prepare runtime cache root", result.Stderr);
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
        AssertExitCode(0, explicitDll);
        Assert.Equal(new[] { fixture.AppDll, "goals", "--repository-root", fixture.Root,
            "--expected-state-directory", state, "--disposable-fixture" }, fixture.NativeArguments());
        Assert.False(Directory.Exists(fixture.CacheRoot));
        Assert.False(File.Exists(fixture.BuildLog));
    }

    private static void AssertExitCode(int expected, TestChildProcessCapture.Result result) =>
        Assert.True(result.ExitCode == expected,
            $"Expected exit {expected}, actual {result.ExitCode}.\nstdout: {result.Stdout}\nstderr: {result.Stderr}");

    private sealed class CacheFixture : IDisposable
    {
        internal const string Head = "controlled-fixture-head";
        // Leave room for the digest and native-asset suffix in Windows PowerShell 5.1.
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), $"fc {Guid.NewGuid().ToString("N")[..12]}");
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
                        // Git must be an executable: invoking git.cmd without CALL in
                        // the production launcher transfers control out of that batch.
                        if (string.Equals(Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule.FileName),
                            "git.exe", StringComparison.OrdinalIgnoreCase)) {
                            if (args.Length < 3 || args[0] != "-C") return 91;
                            if (args[2] == "rev-parse") {
                                Console.WriteLine(Environment.GetEnvironmentVariable("FIXTURE_GIT_HEAD"));
                                return 0;
                            }
                            return args[2] == "diff" ? 0 : 91;
                        }
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
            AssertExitCode(0, compile);
            Assert.True(File.Exists(NativeHost), compile.Stderr);
            File.Copy(NativeHost, Path.Combine(ShimDirectory, "git.exe"));
            var git = StartInfo(Path.Combine(ShimDirectory, "git.exe"));
            foreach (var argument in new[] { "-C", Repository, "rev-parse", "HEAD" })
                git.ArgumentList.Add(argument);
            var gitHead = TestChildProcessCapture.Run(git);
            AssertExitCode(0, gitHead);
            Assert.Equal(Head, gitHead.Stdout.Trim());
        }

        internal string[] NativeArguments()
        {
            Assert.True(File.Exists(ArgumentsLog), "Native executable did not record argv.");
            return File.ReadAllLines(ArgumentsLog).Select(line => Encoding.UTF8.GetString(Convert.FromBase64String(line))).ToArray();
        }

        internal TestChildProcessCapture.Result BoundHelper(params string[] arguments) =>
            RunPowerShell(Path.Combine(Repository, "scripts", "Invoke-OrchestratorCommand.ps1"), arguments);

        private TestChildProcessCapture.Result RunPowerShell(string script, string[] arguments, int exitCode = 0)
        {
            var start = StartInfo(PowerShell);
            start.Environment["FIXTURE_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            foreach (var argument in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script }.Concat(arguments))
                start.ArgumentList.Add(argument);
            return TestChildProcessCapture.Run(start, TimeSpan.FromMinutes(3));
        }

        internal TestChildProcessCapture.Result Launcher(string[] arguments, int exitCode = 0, bool intree = true) =>
            LauncherRaw(string.Join(" ", arguments.Select(value => "\"" + value + "\"")), exitCode, intree);

        internal TestChildProcessCapture.Result LauncherRaw(string arguments, int exitCode = 0, bool intree = true)
        {
            var start = StartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"));
            start.Environment["FIXTURE_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
            start.Environment["MCG_ORCHESTRATOR_LAUNCHER_INTREE"] = intree ? "1" : "0";
            start.Arguments = $"/d /s /c \"\"{Path.Combine(Repository, "mcg-orchestrator.cmd")}\" {arguments}\"";
            return TestChildProcessCapture.Run(start, TimeSpan.FromMinutes(3));
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
            // Leave adoption eligible by default; launcher calls select their mode explicitly.
            start.Environment["MCG_ORCHESTRATOR_LAUNCHER_INTREE"] = "0";
            start.Environment["FIXTURE_NATIVE_HOST"] = NativeHost;
            start.Environment["FIXTURE_GIT_HEAD"] = Head;
            start.Environment["FIXTURE_APP_DLL"] = AppDll;
            start.Environment["FIXTURE_ARGV_LOG"] = ArgumentsLog;
            start.Environment["FIXTURE_BUILD_LOG"] = BuildLog;
            start.Environment["FIXTURE_OBSERVED_APP"] = ObservedApp;
            start.Environment["FIXTURE_NATIVE_ENV"] = NativeEnvironment;
            start.Environment["FIXTURE_EXIT"] = "0";
            return start;
        }

        public void Dispose() => SharedTestSupport.RemoveTempDirectory(Root);
    }
}
