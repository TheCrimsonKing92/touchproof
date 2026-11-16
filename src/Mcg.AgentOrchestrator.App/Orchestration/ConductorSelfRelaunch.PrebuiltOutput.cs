using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial record ConductorSelfRelaunchOptions
{
    internal string? PrebuiltAppOutputDirectory { get; init; }
    internal LandingAppBuildStore? LandingAppBuildStore { get; init; }
}

internal sealed partial record ConductorSuccessorStagingOptions
{
    internal string? RequiredCheckoutBranch { get; init; }
    internal string? PrebuiltAppOutputDirectory { get; init; }
    internal LandingAppBuildStore? LandingAppBuildStore { get; init; }
}

internal sealed record AppBuildCommand(IReadOnlyList<string> Arguments, string IsolatedArtifactsRoot);

internal static partial class ConductorSelfRelaunch
{
    private static int _appBuildInvocationCount;

    internal static int AppBuildInvocationCount => Volatile.Read(ref _appBuildInvocationCount);

    internal static AppBuildCommand CreateAppBuildCommand(string appProjectPath, string outputDirectory)
    {
        var isolatedArtifactsRoot = AppBuildArtifactsRoot(appProjectPath);
        return new(
            [
                "build",
                appProjectPath,
                "--nologo",
                "--output",
                outputDirectory,
                $"-p:McgIsolatedArtifactsPath={isolatedArtifactsRoot}",
                "-v",
                "quiet",
                "-clp:ErrorsOnly"
            ],
            isolatedArtifactsRoot);
    }

    private static string AppBuildArtifactsRoot(string appProjectPath) =>
        Path.Combine(RelaunchBuildCacheIdentity.Parent, RelaunchBuildCacheIdentity.ProjectKey(appProjectPath));

    private static CapturedProcessResult RunCachedAppBuild(
        string dotnetPath, string appProjectPath, string outputDirectory,
        string repositoryRoot, TimeSpan timeout, CancellationToken cancellationToken,
        string? qosStatePath = null, Func<int, uint, uint, bool>? qosSetter = null)
    {
        var elapsed = Stopwatch.StartNew();
        var cacheRoot = AppBuildArtifactsRoot(appProjectPath);
        var cachedOutput = Path.Combine(cacheRoot, "output");
        var command = CreateAppBuildCommand(appProjectPath, cachedOutput);
        Directory.CreateDirectory(cacheRoot);
        // Direct and store builds share intermediates: hold the cross-process lock through copying.
        FileStream cacheLock;
        var attemptedLock = false;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // A free lock with an exhausted budget belongs to the pre-launch check.
            if (attemptedLock && timeout != Timeout.InfiniteTimeSpan && elapsed.Elapsed >= timeout)
                return WithBuildDiagnostics(new(-1, "", "Timed out waiting for isolated App build cache.", TimedOut: true),
                    dotnetPath, command, elapsed, timeout, "cache-lock-wait", planned: true);
            attemptedLock = true;
            try
            {
                cacheLock = File.Open(Path.Combine(cacheRoot, RelaunchBuildCacheIdentity.LockFileName),
                    FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (DirectoryNotFoundException)
            {
                Directory.CreateDirectory(cacheRoot);
            }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 or 11)
            {
                var delay = timeout == Timeout.InfiniteTimeSpan ? 25 : Math.Min(25, Math.Max(1, (timeout - elapsed.Elapsed).TotalMilliseconds));
                if (cancellationToken.WaitHandle.WaitOne(TimeSpan.FromMilliseconds(delay)))
                    cancellationToken.ThrowIfCancellationRequested();
            }
        }

        using (cacheLock)
        {
            // Remove stale final payloads while retaining obj; MSBuild copies up-to-date assemblies back.
            if (Directory.Exists(cachedOutput)) Directory.Delete(cachedOutput, recursive: true);
            var remaining = timeout == Timeout.InfiniteTimeSpan ? timeout : timeout - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan)
                return WithBuildDiagnostics(new(-1, "", "Timed out waiting for isolated App build cache.", TimedOut: true),
                    dotnetPath, command, elapsed, timeout, "pre-launch-deadline", planned: true);
            CapturedProcessResult result;
            try
            {
                result = RunProcess(dotnetPath, command.Arguments, repositoryRoot, remaining, cancellationToken,
                    onStarted: qosStatePath is null ? null : pid => BuildLaunchQosSweep.Begin(
                        "relaunch-build", qosStatePath, string.Empty, pid, BuildLaunchQosSweep.RootAndDescendants, qosSetter));
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
            {
                var message = WithBuildDiagnostics(new(-1, "", ex.Message),
                    dotnetPath, command, elapsed, timeout, phase: null, planned: true).Stderr;
                // Launch faults must retain their infrastructure classification at the canary boundary.
                if (ex is System.ComponentModel.Win32Exception native)
                    throw new System.ComponentModel.Win32Exception(native.NativeErrorCode, message);
                var missing = (FileNotFoundException)ex;
                throw new FileNotFoundException(message, missing.FileName, missing);
            }
            if (result.ExitCode == 0 && !result.TimedOut)
            {
                CopyAppBuildOutput(cachedOutput, outputDirectory, cancellationToken, overwrite: true);
                return result;
            }
            return WithBuildDiagnostics(result, dotnetPath, command, elapsed, timeout,
                result.TimedOut && result.ExitCode == -1 ? "build-process" : null, planned: false);
        }
    }

    private static CapturedProcessResult WithBuildDiagnostics(
        CapturedProcessResult result, string dotnetPath, AppBuildCommand command,
        Stopwatch elapsed, TimeSpan timeout, string? phase, bool planned)
    {
        // Reuse the launcher's argv escaping so quotes and trailing slashes retain their boundaries.
        var invocation = new ProcessStartInfo(dotnetPath);
        foreach (var argument in command.Arguments) invocation.ArgumentList.Add(argument);
        var deadline = timeout == Timeout.InfiniteTimeSpan
            ? "infinite"
            : timeout.TotalMilliseconds.ToString("0.################", CultureInfo.InvariantCulture);
        var diagnostics = $"{(planned ? "plannedCommand" : "command")}={HandleListProcessLauncher.CommandLine(invocation)} " +
            $"artifactsRoot={command.IsolatedArtifactsRoot} " +
            (phase is null ? "" : $"phase={phase} ") +
            $"elapsedMs={elapsed.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)} deadlineMs={deadline}";
        // EnsureSucceeded reads stderr when present, otherwise stdout; keep compiler output visible.
        return !string.IsNullOrWhiteSpace(result.Stderr) || string.IsNullOrWhiteSpace(result.Stdout)
            ? result with { Stderr = string.IsNullOrEmpty(result.Stderr) ? diagnostics : result.Stderr + Environment.NewLine + diagnostics }
            : result with { Stdout = result.Stdout + Environment.NewLine + diagnostics };
    }

    private static void CopyAppBuildOutput(string source, string destinationRoot, CancellationToken cancellationToken,
        bool skipStoreMarker = false, bool overwrite = false)
    {
        foreach (var sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(source, sourceFile);
            if (skipStoreMarker && relativePath == LandingAppBuildStore.CompleteMarkerName) continue;
            var destination = Path.Combine(destinationRoot, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(sourceFile, destination, overwrite);
            File.SetAttributes(destination, FileAttributes.Normal);
        }
    }

    private static CapturedProcessResult ProduceBuildOutput(
        ConductorSuccessorStagingOptions options,
        string buildOutputDirectory,
        TimeSpan buildTimeout,
        CancellationToken cancellationToken,
        bool skipStoreMarker = false)
    {
        if (options.PrebuiltAppOutputDirectory is null)
        {
            Interlocked.Increment(ref _appBuildInvocationCount);
            return RunCachedAppBuild(options.DotnetPath, options.AppProjectPath, buildOutputDirectory,
                options.RepositoryRoot, buildTimeout, cancellationToken, options.StateStorePath);
        }

        var source = options.PrebuiltAppOutputDirectory;
        var appDllName = Path.GetFileName(options.AppDllPath);
        if (!Directory.Exists(source) || !File.Exists(Path.Combine(source, appDllName)))
        {
            throw new ConductorSelfRelaunchPreparationException(
                "build",
                $"Prebuilt App output at {source} is missing or does not contain {appDllName}.");
        }

        try
        {
            CopyAppBuildOutput(source, buildOutputDirectory, cancellationToken, skipStoreMarker);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new ConductorSelfRelaunchPreparationException(
                "build",
                $"Prebuilt App output at {source} could not be copied: {ex.Message}");
        }

        return new CapturedProcessResult(0, $"prebuilt:{source}", "");
    }

    internal static LandingAppBuildResult RunAppBuildProcess(
        string dotnetPath, LandingAppBuildRequest request, CancellationToken cancellationToken,
        string? qosStatePath = null, Func<int, uint, uint, bool>? qosSetter = null)
    {
        var result = RunCachedAppBuild(dotnetPath,
            Path.Combine(request.SourceRoot, "src", "Mcg.AgentOrchestrator.App", "Mcg.AgentOrchestrator.App.csproj"),
            request.OutputDirectory, request.SourceRoot, request.Timeout, cancellationToken, qosStatePath, qosSetter);
        return new(result.ExitCode, result.Stdout, result.Stderr, result.TimedOut);
    }
}
