using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;


[Xunit.Collection(TestCollections.ProcessSpawning)]
public sealed class GoalWorktreeIsolatedDotnetTests : GoalWorktreeTestBase
{
    // Goal receipts after the minimal-probe conversion complete in 0.66-0.91s; this is a hang guard, not a performance bound.
    private static readonly TimeSpan RealProcessExitHangGuard = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan ProcessReadinessHangGuard = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan RealProcessControlShortHangGuard = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RealProcessControlCompletionDelay = TimeSpan.FromSeconds(5);
    private const string ProbeProjectName = "Mcg.AgentOrchestrator.IsolatedDotnetProbe";
    private const string ProbeProject = "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/Fixtures/IsolatedDotnetProbe/Mcg.AgentOrchestrator.IsolatedDotnetProbe.csproj";

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_reuses_prebuilt_test_assembly_and_dependency_directory")]
    public async Task InvokeIsolatedDotnetReusesPrebuiltTestAssemblyAndDependencyDirectory()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = CreateReuseFixture(includeAssembly: true);
        try
        {
            var result = await RunReusePassAsync(fixture);

            Assert.True(
                result.ExitCode == 0,
                $"Invoke-IsolatedDotnet.ps1 exited {result.ExitCode}.{Environment.NewLine}stdout:{Environment.NewLine}{result.Stdout}{Environment.NewLine}stderr:{Environment.NewLine}{result.Stderr}");
            Assert.Contains(fixture.AssemblyPath, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(fixture.ExecutablePath, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(fixture.DependencyDirectory, result.Stdout, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(fixture.AssemblyPath), "The reuse pass must preserve the pre-built test assembly.");
            Assert.True(File.Exists(fixture.ExecutablePath), "The reuse pass must preserve the pre-built MTP executable.");
            Assert.True(
                File.Exists(Path.Combine(fixture.DependencyDirectory, $"{ProbeProjectName}.deps.json")),
                "The reuse pass must preserve the test assembly dependency directory.");
            var probeArguments = File.ReadAllLines(fixture.ProbeReceiptPath);
            Assert.Contains("--filter-class", probeArguments);
            Assert.Contains("*IsolatedDotnetProbe*", probeArguments);
            Assert.Contains("--no-ansi", probeArguments);
            Assert.Contains("--progress", probeArguments);
            Assert.Contains("off", probeArguments);

            var log = File.ReadAllText(fixture.DotnetLogPath);
            Assert.DoesNotContain("args=test ", log, StringComparison.Ordinal);
            Assert.DoesNotContain("args=build ", log, StringComparison.Ordinal);
            Assert.DoesNotContain("args=build-server shutdown", log, StringComparison.Ordinal);
            Assert.Contains($"args={fixture.AssemblyPath} ", log, StringComparison.OrdinalIgnoreCase);

            var source = File.ReadAllText(Path.Combine(FindCurrentSourceRoot(), "scripts", "Invoke-IsolatedDotnet.ps1"));
            Assert.Contains(".SYNOPSIS", source, StringComparison.Ordinal);
            Assert.Contains(".DESCRIPTION", source, StringComparison.Ordinal);
            Assert.True(source.Split(".EXAMPLE", StringSplitOptions.None).Length >= 3);
            Assert.Contains("-ReuseArtifacts", source, StringComparison.Ordinal);
            Assert.Contains("--no-build", source, StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_reuse_fails_loudly_when_test_assembly_is_missing")]
    public async Task InvokeIsolatedDotnetReuseFailsLoudlyWhenTestAssemblyIsMissing()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = CreateReuseFixture(includeAssembly: false);
        try
        {
            var result = await RunReusePassAsync(fixture);

            Assert.Equal(86, result.ExitCode);
            Assert.Contains("Artifact reuse precondition failed", result.Stderr, StringComparison.Ordinal);
            Assert.Contains(fixture.AssemblyPath, result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Expected owner token: 'goal-reuse-goal'", result.Stderr, StringComparison.Ordinal);
            Assert.Contains("found owner token: 'goal-reuse-goal'", result.Stderr, StringComparison.Ordinal);
            Assert.Contains(
                $@".\scripts\Invoke-IsolatedDotnet.ps1 -GoalPrefix reuse-goal build {ProbeProject} --configuration Debug --verbosity minimal",
                result.Stderr,
                StringComparison.Ordinal);

            Assert.False(
                File.Exists(fixture.DotnetLogPath),
                "Artifact validation must fail before starting dotnet or shutting down session-global build servers.");
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_reuse_requires_the_generated_apphost_as_an_artifact_invariant")]
    public async Task InvokeIsolatedDotnetReuseRequiresGeneratedApphost()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = CreateReuseFixture(includeAssembly: true, includeExecutable: false);
        try
        {
            var result = await RunReusePassAsync(fixture);

            Assert.Equal(86, result.ExitCode);
            Assert.Contains("The reusable Microsoft.Testing.Platform executable was not found.", result.Stderr, StringComparison.Ordinal);
            Assert.Contains(fixture.ExecutablePath, result.Stderr, StringComparison.OrdinalIgnoreCase);
            Assert.False(File.Exists(fixture.ProbeReceiptPath));
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Xunit.Fact(DisplayName = "InvokeIsolatedDotnet_reuse_propagates_dotnet_host_exit_code")]
    public async Task InvokeIsolatedDotnetReusePropagatesDotnetHostExitCode()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const int expectedExitCode = 37;
        var fixture = CreateReuseFixture(includeAssembly: true);
        try
        {
            var result = await RunReusePassAsync(fixture, probeExitCode: expectedExitCode);

            Assert.Equal(expectedExitCode, result.ExitCode);
            Assert.True(File.Exists(fixture.ProbeReceiptPath));
            Assert.Contains(
                $"args={fixture.AssemblyPath} ",
                File.ReadAllText(fixture.DotnetLogPath),
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Xunit.Fact(DisplayName = "Reuse_hang_guard_fires_when_real_dependency_outlives_injected_budget")]
    public async Task ReuseHangGuardFiresWhenRealDependencyOutlivesInjectedBudget()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var fixture = CreateReuseFixture(includeAssembly: true, shimDelay: RealProcessControlCompletionDelay);
        try
        {
            Assert.True(
                RealProcessControlCompletionDelay > RealProcessControlShortHangGuard,
                "The real shim delay must exceed the injected guard by construction.");

            var exception = await Assert.ThrowsAsync<RealProcessHangGuardException>(
                () => RunReusePassAsync(
                    fixture,
                    RealProcessControlShortHangGuard,
                    waitForShimReadiness: true));

            Assert.Contains(
                $"{RealProcessControlShortHangGuard.TotalSeconds:0} seconds",
                exception.Message,
                StringComparison.Ordinal);
            Assert.Contains("elapsed=", exception.Message, StringComparison.Ordinal);
            Assert.True(exception.RootExited, "The short-guard PowerShell root must be reaped.");
            Assert.True(exception.DescendantExited, "The delayed shim descendant must be reaped.");

            var result = await RunReusePassAsync(fixture);

            Assert.True(result.ExitCode == 0, result.Stdout + result.Stderr);
            Assert.Contains(
                $"delaySeconds={RealProcessControlCompletionDelay.TotalSeconds:0}",
                File.ReadAllText(fixture.DotnetLogPath),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    [Xunit.Fact(DisplayName = "Reuse_hang_guard_fails_and_reaps_when_dependency_never_exits")]
    public async Task ReuseHangGuardFailsAndReapsWhenDependencyNeverExits()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var guard = TimeSpan.FromSeconds(2);
        var fixture = CreateReuseFixture(includeAssembly: true, shimNeverExits: true);
        try
        {
            var exception = await Assert.ThrowsAsync<RealProcessHangGuardException>(
                () => RunReusePassAsync(fixture, guard, waitForShimReadiness: true));

            Assert.Contains($"{guard.TotalSeconds:0} seconds", exception.Message, StringComparison.Ordinal);
            Assert.Contains("elapsed=", exception.Message, StringComparison.Ordinal);
            Assert.True(exception.RootExited, "The guarded PowerShell root must be reaped.");
            Assert.True(exception.DescendantExited, "The never-exiting shim descendant must be reaped.");
        }
        finally
        {
            DeleteDirectory(fixture.Root);
        }
    }

    private static ReuseFixture CreateReuseFixture(
        bool includeAssembly,
        TimeSpan? shimDelay = null,
        bool shimNeverExits = false,
        bool includeExecutable = true)
    {
        Assert.False(shimDelay.HasValue && shimNeverExits, "A shim cannot be delayed and never-exiting at the same time.");
        var root = Path.Combine(Path.GetTempPath(), "mcg-isolated-dotnet-reuse-tests", Guid.NewGuid().ToString("N"));
        var isolatedRoot = Path.Combine(root, "isolated");
        var shimDirectory = Path.Combine(root, "shim");
        var workDirectory = Path.Combine(root, "repo");
        var artifactsPath = Path.Combine(isolatedRoot, "goals", "reuse-goal", "artifacts");
        var dependencyDirectory = Path.Combine(artifactsPath, "bin", ProbeProjectName, "debug");
        var assemblyPath = Path.Combine(dependencyDirectory, $"{ProbeProjectName}.dll");
        var executablePath = Path.Combine(dependencyDirectory, $"{ProbeProjectName}.exe");
        var dotnetLogPath = Path.Combine(root, "dotnet.log");
        var probeReceiptPath = Path.Combine(root, "mtp-probe.txt");
        var shimReadyPath = Path.Combine(root, "dotnet-shim.ready");
        var shimChildPidPath = Path.Combine(root, "dotnet-shim-child.pid");

        Directory.CreateDirectory(shimDirectory);
        Directory.CreateDirectory(workDirectory);
        Directory.CreateDirectory(dependencyDirectory);
        File.WriteAllText(
            Path.Combine(artifactsPath, ".mcg-artifacts-owner.json"),
            JsonSerializer.Serialize(new
            {
                version = 1,
                ownerToken = "goal-reuse-goal",
                ownerProcessId = 123456789,
                machineName = Environment.MachineName,
                lastAcquiredAt = DateTimeOffset.UtcNow
            }));
        CopyProbeOutput(ResolveProbeOutputDirectory(), dependencyDirectory);
        Assert.True(File.Exists(assemblyPath), $"Probe output is missing {assemblyPath}.");
        Assert.True(File.Exists(executablePath), $"Probe output is missing {executablePath}.");
        if (!includeAssembly)
        {
            File.Delete(assemblyPath);
        }
        if (!includeExecutable)
        {
            File.Delete(executablePath);
        }

        var shimLines = new List<string>
        {
            "@echo off",
            ">> \"%DOTNET_SHIM_LOG%\" echo args=%*"
        };
        if (shimDelay.HasValue)
        {
            shimLines.Add($">> \"%DOTNET_SHIM_LOG%\" echo delaySeconds={shimDelay.Value.TotalSeconds:0}");
            shimLines.Add(
                $"powershell.exe -NoProfile -NonInteractive -Command \"Set-Content -LiteralPath $env:DOTNET_SHIM_CHILD_PID -Value $PID; " +
                $"Set-Content -LiteralPath $env:DOTNET_SHIM_READY_PATH -Value ready; Start-Sleep -Seconds {shimDelay.Value.TotalSeconds:0}\"");
        }
        else if (shimNeverExits)
        {
            shimLines.Add(
                "powershell.exe -NoProfile -NonInteractive -Command \"Set-Content -LiteralPath $env:DOTNET_SHIM_CHILD_PID -Value $PID; " +
                "Set-Content -LiteralPath $env:DOTNET_SHIM_READY_PATH -Value ready; while ($true) { Start-Sleep -Seconds 60 }\"");
        }
        if (shimDelay.HasValue || shimNeverExits)
        {
            shimLines.Add("exit /b 0");
        }
        else
        {
            shimLines.Add("set \"PATH=%DOTNET_REAL_PATH%\"");
            shimLines.Add("dotnet.exe %*");
            shimLines.Add("exit /b %ERRORLEVEL%");
        }
        File.WriteAllLines(Path.Combine(shimDirectory, "dotnet.cmd"), shimLines);

        return new ReuseFixture(
            root,
            isolatedRoot,
            shimDirectory,
            workDirectory,
            artifactsPath,
            dependencyDirectory,
            assemblyPath,
            executablePath,
            dotnetLogPath,
            probeReceiptPath,
            shimReadyPath,
            shimChildPidPath);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunReusePassAsync(
        ReuseFixture fixture,
        TimeSpan? hangGuard = null,
        bool waitForShimReadiness = false,
        int? probeExitCode = null)
    {
        var activeHangGuard = hangGuard ?? RealProcessExitHangGuard;
        var startInfo = new ProcessStartInfo
        {
            FileName = WorkerShell.Executable,
            WorkingDirectory = fixture.WorkDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-InputFormat");
        startInfo.ArgumentList.Add("None");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(FindCurrentSourceRoot(), "scripts", "Invoke-IsolatedDotnet.ps1"));
        startInfo.ArgumentList.Add("-GoalPrefix");
        startInfo.ArgumentList.Add("reuse-goal");
        startInfo.ArgumentList.Add("-ReuseArtifacts");
        startInfo.ArgumentList.Add("test");
        startInfo.ArgumentList.Add(ProbeProject);
        startInfo.ArgumentList.Add("--no-build");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Debug");
        startInfo.ArgumentList.Add("--verbosity");
        startInfo.ArgumentList.Add("minimal");
        startInfo.ArgumentList.Add("--filter");
        startInfo.ArgumentList.Add("FullyQualifiedName~IsolatedDotnetProbe");
        var realDotnetPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        startInfo.Environment["PATH"] = fixture.ShimDirectory + Path.PathSeparator + realDotnetPath;
        startInfo.Environment["DOTNET_REAL_PATH"] = realDotnetPath;
        startInfo.Environment["DOTNET_SHIM_LOG"] = fixture.DotnetLogPath;
        startInfo.Environment["DOTNET_SHIM_READY_PATH"] = fixture.ShimReadyPath;
        startInfo.Environment["DOTNET_SHIM_CHILD_PID"] = fixture.ShimChildPidPath;
        startInfo.Environment["MCG_ISOLATED_DOTNET_MTP_PROBE_PATH"] = fixture.ProbeReceiptPath;
        if (probeExitCode.HasValue)
        {
            startInfo.Environment["MCG_ISOLATED_DOTNET_MTP_PROBE_EXIT_CODE"] = probeExitCode.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        startInfo.Environment[DotnetBuildEnvironmentManager.IsolatedRootOverrideVariable] = fixture.IsolatedRoot;
        startInfo.Environment.Remove(WorkerSandboxOptions.DispatchWorkerVariable);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Failed to start PowerShell.");
        var rootIdentity = TestOwnedProcessStop.Identify(process);
        process.StandardInput.Close();
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        Process? descendant = null;
        if (waitForShimReadiness)
        {
            try
            {
                await WaitForFilesAsync(
                    fixture.Root,
                    [fixture.ShimReadyPath, fixture.ShimChildPidPath],
                    ProcessReadinessHangGuard);
            }
            catch
            {
                if (!process.HasExited)
                {
                    Assert.True(TestOwnedProcessStop.StopTreeIfSame(rootIdentity) || process.HasExited,
                        "Could not stop the recorded PowerShell process after readiness failed.");
                }
                await process.WaitForExitAsync();
                await stdoutTask;
                await stderrTask;
                throw;
            }
            descendant = Process.GetProcessById(int.Parse(File.ReadAllText(fixture.ShimChildPidPath).Trim(), System.Globalization.CultureInfo.InvariantCulture));
        }

        var stopwatch = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(activeHangGuard);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                Assert.True(TestOwnedProcessStop.StopTreeIfSame(rootIdentity) || process.HasExited,
                    "Could not stop the recorded PowerShell process after the hang guard expired.");
            }

            await process.WaitForExitAsync();
            var descendantExited = descendant is null || descendant.WaitForExit(10_000);
            var timedOutStdout = await stdoutTask;
            var timedOutStderr = await stderrTask;
            throw new RealProcessHangGuardException(
                $"Invoke-IsolatedDotnet.ps1 did not exit within {activeHangGuard.TotalSeconds:0} seconds; elapsed={stopwatch.Elapsed}.{Environment.NewLine}stdout:{Environment.NewLine}{timedOutStdout}{Environment.NewLine}stderr:{Environment.NewLine}{timedOutStderr}",
                process.HasExited,
                descendantExited);
        }
        finally
        {
            descendant?.Dispose();
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return (process.ExitCode, stdout, stderr);
    }

    private static async Task WaitForFilesAsync(string directory, string[] paths, TimeSpan hangGuard)
    {
        if (paths.All(File.Exists))
        {
            return;
        }

        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new FileSystemWatcher(directory)
        {
            EnableRaisingEvents = true,
            IncludeSubdirectories = false
        };
        FileSystemEventHandler checkReady = (_, _) =>
        {
            if (paths.All(File.Exists))
            {
                ready.TrySetResult();
            }
        };
        watcher.Created += checkReady;
        watcher.Changed += checkReady;
        if (paths.All(File.Exists))
        {
            return;
        }

        using var timeout = new CancellationTokenSource(hangGuard);
        using var registration = timeout.Token.Register(
            () => ready.TrySetException(new Xunit.Sdk.XunitException(
                $"Real-process readiness files did not arrive within {hangGuard.TotalSeconds:0} seconds: {string.Join(", ", paths)}")));
        await ready.Task;
    }

    private static void CopyProbeOutput(string source, string destination)
    {
        // The probe has no project/package dependencies. A sealed test closure also
        // contains the test host and application; copy only this probe's artifacts.
        foreach (var file in Directory.EnumerateFiles(source, $"{ProbeProjectName}.*", SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(destination, Path.GetFileName(file));
            File.Copy(file, target);
        }
    }

    private static string ResolveProbeOutputDirectory()
    {
        string[] requiredFiles = [$"{ProbeProjectName}.dll", $"{ProbeProjectName}.exe"];
        if (FixtureProbeLocator.FindDirectory(typeof(GoalWorktreeIsolatedDotnetTests), ProbeProjectName, requiredFiles) is { } directory)
            return directory;

        throw new Xunit.Sdk.XunitException("Locator tried: " +
            string.Join("; ", FixtureProbeLocator.TriedPaths(typeof(GoalWorktreeIsolatedDotnetTests), ProbeProjectName, requiredFiles)));
    }

    private sealed record ReuseFixture(
        string Root,
        string IsolatedRoot,
        string ShimDirectory,
        string WorkDirectory,
        string ArtifactsPath,
        string DependencyDirectory,
        string AssemblyPath,
        string ExecutablePath,
        string DotnetLogPath,
        string ProbeReceiptPath,
        string ShimReadyPath,
        string ShimChildPidPath);

    private sealed class RealProcessHangGuardException(
        string message,
        bool rootExited,
        bool descendantExited) : Xunit.Sdk.XunitException(message)
    {
        public bool RootExited { get; } = rootExited;
        public bool DescendantExited { get; } = descendantExited;
    }
}
