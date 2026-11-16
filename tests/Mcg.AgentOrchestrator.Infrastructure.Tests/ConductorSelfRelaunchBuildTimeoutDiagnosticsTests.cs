using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

// Parallel-safe: each fact owns its repository, native helper, output and project-keyed cache.
// The store route avoids the process-wide direct-build counter used by the relaunch fixture.
public sealed class ConductorSelfRelaunchBuildTimeoutDiagnosticsTests
{
    [Fact]
    public void Held_cache_lock_reports_lock_wait_and_planned_command()
    {
        using var fixture = new BuildFixture();
        var request = fixture.Request(TimeSpan.FromMilliseconds(50));
        var command = fixture.Command(request);
        Directory.CreateDirectory(command.IsolatedArtifactsRoot);
        using var heldLock = File.Open(Path.Combine(command.IsolatedArtifactsRoot, "build.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var result = ConductorSelfRelaunch.RunAppBuildProcess(fixture.MissingExecutable,
            request, CancellationToken.None);

        AssertTimeout(result, "cache-lock-wait", "50");
        Assert.Contains("Timed out waiting for isolated App build cache.", result.Stderr);
        AssertCommand(result, fixture.MissingExecutable, command, planned: true);
    }

    [Fact]
    public void Exhausted_budget_with_free_lock_reports_pre_launch_deadline()
    {
        using var fixture = new BuildFixture();
        var request = fixture.Request(TimeSpan.Zero);

        var result = ConductorSelfRelaunch.RunAppBuildProcess(fixture.MissingExecutable,
            request, CancellationToken.None);

        AssertTimeout(result, "pre-launch-deadline", "0");
        AssertCommand(result, fixture.MissingExecutable, fixture.Command(request), planned: true);
    }

    [Fact]
    public void Hanging_build_reports_process_timeout_and_retains_failure_fields()
    {
        using var fixture = new BuildFixture();
        var executable = fixture.CreateExecutable(hang: true);
        var request = fixture.Request(TimeSpan.FromSeconds(3));

        var result = ConductorSelfRelaunch.RunAppBuildProcess(executable, request, CancellationToken.None);

        AssertTimeout(result, "build-process", "3000");
        var command = fixture.Command(request);
        AssertCommand(result, executable, command, planned: false);
        var detail = Detail(result);
        Assert.True(detail.Length < 1000,
            $"Realistic diagnostic block must fit the existing tail; length={detail.Length}, rootLength={fixture.Root.Length}.");
        var failure = fixture.FailureMessage(result);
        Assert.Contains(detail, failure);
        Assert.Contains("build merged conductor failed exit=-1 timedOut=True", failure);
    }

    [Fact]
    public void Oversized_arguments_keep_artifacts_phase_and_times_in_failure_tail()
    {
        using var fixture = new BuildFixture();
        // This path is never created or launched; only its hashed cache directory exists.
        var longRoot = Path.Combine(fixture.Root, "argument-start-" + new string('x', 1200));
        var request = fixture.Request(TimeSpan.Zero) with { SourceRoot = longRoot };

        var result = ConductorSelfRelaunch.RunAppBuildProcess(fixture.MissingExecutable,
            request, CancellationToken.None);

        AssertTimeout(result, "pre-launch-deadline", "0");
        var command = fixture.Command(request);
        AssertCommand(result, fixture.MissingExecutable, command, planned: true);
        var detail = Detail(result);
        Assert.True(detail.Length > 1000, $"Oversized diagnostic block length={detail.Length}.");
        var failure = fixture.FailureMessage(result);
        Assert.DoesNotContain("argument-start-", failure);
        Assert.EndsWith(detail[^1000..], failure);
        Assert.Contains("artifactsRoot=" + command.IsolatedArtifactsRoot, failure);
        Assert.Contains("phase=pre-launch-deadline", failure);
        Assert.Contains("deadlineMs=0", failure);
        AssertElapsed(failure);
    }

    [Fact]
    public void Missing_executable_reports_start_failure_without_timeout_phase()
    {
        using var fixture = new BuildFixture();
        var request = fixture.Request(Timeout.InfiniteTimeSpan);

        var failure = Assert.Throws<Win32Exception>(() =>
            ConductorSelfRelaunch.RunAppBuildProcess(fixture.MissingExecutable,
                request, CancellationToken.None));

        Assert.Equal(2, failure.NativeErrorCode);
        Assert.StartsWith(new Win32Exception(2).Message, failure.Message);
        Assert.DoesNotContain("phase=", failure.Message);
        Assert.Contains("deadlineMs=infinite", failure.Message);
        AssertElapsed(failure.Message);
        AssertCommand(failure.Message, fixture.MissingExecutable, fixture.Command(request), planned: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Compiler_failure_keeps_selected_output_and_command(bool stderr)
    {
        using var fixture = new BuildFixture();
        var executable = fixture.CreateExecutable(hang: false, stderr: stderr);
        // Hang detector for the helper's exit, not a performance assertion.
        var request = fixture.Request(TimeSpan.FromSeconds(30));

        var result = ConductorSelfRelaunch.RunAppBuildProcess(executable, request, CancellationToken.None);

        Assert.Equal(23, result.ExitCode);
        Assert.False(result.TimedOut);
        Assert.Contains("fixture compiler error", Detail(result));
        Assert.DoesNotContain("phase=", Detail(result));
        AssertCommand(result, executable, fixture.Command(request), planned: false);
        Assert.Contains("fixture compiler error", fixture.FailureMessage(result));
    }

    private static string Detail(LandingAppBuildResult result) =>
        string.IsNullOrWhiteSpace(result.Stderr) ? result.Stdout : result.Stderr;

    private static void AssertTimeout(LandingAppBuildResult result, string phase, string deadline)
    {
        Assert.Equal(-1, result.ExitCode);
        Assert.True(result.TimedOut, result.Stdout + result.Stderr);
        var detail = Detail(result);
        Assert.Contains("phase=" + phase, detail);
        Assert.Contains("deadlineMs=" + deadline, detail);
        Assert.EndsWith("deadlineMs=" + deadline, detail);
        AssertElapsed(detail);
        foreach (var other in new[] { "cache-lock-wait", "pre-launch-deadline", "build-process" })
            if (other != phase) Assert.DoesNotContain("phase=" + other, detail);
    }

    private static void AssertElapsed(string detail)
    {
        var elapsed = Regex.Match(detail, @"\belapsedMs=(\d+)\b");
        Assert.True(elapsed.Success, detail);
        Assert.True(long.TryParse(elapsed.Groups[1].Value, NumberStyles.None,
            CultureInfo.InvariantCulture, out var value) && value >= 0, detail);
    }

    private static void AssertCommand(
        LandingAppBuildResult result, string executable, AppBuildCommand command, bool planned)
        => AssertCommand(Detail(result), executable, command, planned);

    private static void AssertCommand(
        string detail, string executable, AppBuildCommand command, bool planned)
    {
        var expected = new ProcessStartInfo(executable) { CreateNoWindow = true };
        foreach (var argument in command.Arguments) expected.ArgumentList.Add(argument);
        Assert.Contains((planned ? "plannedCommand=" : "command=") +
            HandleListProcessLauncher.CommandLine(expected), detail);
        var artifacts = "artifactsRoot=" + command.IsolatedArtifactsRoot;
        Assert.Contains(artifacts, detail);
        Assert.True(detail.IndexOf(artifacts, StringComparison.Ordinal) >
            detail.IndexOf("Command=", StringComparison.OrdinalIgnoreCase), detail);
    }

    private sealed class BuildFixture : IDisposable
    {
        internal string Root { get; } = Directory.CreateTempSubdirectory("r-").FullName;
        internal string MissingExecutable => Path.Combine(Root, "missing-dotnet-executable");
        private readonly HashSet<string> cacheRoots = [];

        internal LandingAppBuildRequest Request(TimeSpan timeout) => new(Root, Path.Combine(Root, "out"), timeout);

        internal AppBuildCommand Command(LandingAppBuildRequest request)
        {
            var project = Path.Combine(request.SourceRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj");
            var root = ConductorSelfRelaunch.CreateAppBuildCommand(project, request.OutputDirectory).IsolatedArtifactsRoot;
            cacheRoots.Add(root);
            return ConductorSelfRelaunch.CreateAppBuildCommand(project, Path.Combine(root, "output"));
        }

        internal string CreateExecutable(bool hang, bool stderr = false)
        {
            // Copy the already-built native test host. Its embedded DLL name is unchanged;
            // emit a tiny replacement in this private directory, never a nested dotnet build.
            var assembly = typeof(ConductorSelfRelaunchBuildTimeoutDiagnosticsTests).Assembly.Location;
            var nativeHost = Path.ChangeExtension(assembly, OperatingSystem.IsWindows() ? ".exe" : null);
            var executable = Path.Combine(Root, OperatingSystem.IsWindows() ? "b build.exe" : "b build");
            File.Copy(nativeHost, executable);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(executable, File.GetUnixFileMode(nativeHost));
            var name = Path.GetFileNameWithoutExtension(assembly);
            File.Copy(Path.ChangeExtension(assembly, ".runtimeconfig.json"), Path.Combine(Root, name + ".runtimeconfig.json"));
            var source = hang
                ? "using var hold = new System.Threading.ManualResetEvent(false); hold.WaitOne();"
                : $"System.Console.{(stderr ? "Error" : "Out")}.WriteLine(\"fixture compiler error\"); return 23;";
            var references = ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")
                ?? throw new InvalidOperationException("Trusted platform assemblies unavailable."))
                .Split(Path.PathSeparator).Select(path => MetadataReference.CreateFromFile(path));
            var compilation = CSharpCompilation.Create(name, [CSharpSyntaxTree.ParseText(source)], references,
                new CSharpCompilationOptions(OutputKind.ConsoleApplication));
            var emitted = compilation.Emit(Path.Combine(Root, Path.GetFileName(assembly)));
            Assert.True(emitted.Success, string.Join(Environment.NewLine, emitted.Diagnostics));
            return executable;
        }

        internal string FailureMessage(LandingAppBuildResult result)
        {
            Git("init", "-b", "main");
            Git("-c", "user.name=Tests", "-c", "user.email=tests@example.invalid",
                "-c", "commit.gpgsign=false", "commit", "--allow-empty", "-m", "Initial");
            var calls = 0;
            var store = new LandingAppBuildStore(Path.Combine(Root, "store"), build: (_, _) =>
            {
                calls++;
                return result;
            });
            var options = new ConductorSuccessorStagingOptions(
                RepositoryRoot: Root, AppProjectPath: Path.Combine(Root, "App.csproj"),
                AppDllPath: Path.Combine(Root, "App.dll"),
                UpdateHeadMarkerScriptPath: Path.Combine(Root, "marker.ps1"),
                ResolveRunDirectoryScriptPath: Path.Combine(Root, "resolver.ps1"),
                StateStorePath: Path.Combine(Root, "state.db"),
                AgentCatalogPath: Path.Combine(Root, "agents.json"),
                WorkerProfilePath: Path.Combine(Root, "workers.json"),
                ModelFunctionCatalogPath: Path.Combine(Root, "functions.json"),
                DotnetPath: MissingExecutable, PowerShellPath: "missing-powershell")
            { LandingAppBuildStore = store };
            var failure = Assert.Throws<ConductorSelfRelaunchPreparationException>(() =>
                ConductorSelfRelaunch.PrepareSuccessor(options));
            Assert.Equal(1, calls);
            Assert.Equal("build", failure.Phase);
            return failure.Message;
        }

        private void Git(params string[] args)
        {
            var result = InfrastructureTestSupport.RunGitProbe(Root, ["-C", Root, .. args]);
            InfrastructureTestSupport.RequireCompleteGitOutput(result);
            Assert.True(result.Succeeded, $"git probe failed: {result}");
        }

        public void Dispose()
        {
            Command(Request(TimeSpan.Zero));
            foreach (var root in cacheRoots)
                if (Directory.Exists(root)) ScriptSandboxCleanup.DeleteDirectoryTree(root);
            ScriptSandboxCleanup.DeleteDirectoryTree(Root);
        }
    }
}
