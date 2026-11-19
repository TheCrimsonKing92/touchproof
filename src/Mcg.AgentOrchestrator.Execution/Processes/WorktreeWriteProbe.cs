using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.Infrastructure;

/// <summary>
/// Checks actual writes under the worker's Low-integrity token before provider startup.
/// See docs/operator-runbook.md#worktree-write-boundary-invariant.
/// </summary>
internal static class WorktreeWriteProbe
{
    internal const string InsideWriteDenied = "inside-write-denied";
    internal const string OutsideWriteAllowed = "outside-write-allowed";
    internal const string SentinelNotCleaned = "sentinel-not-cleaned";
    internal const string ResultPrefix = "worktree-write-probe: ";

    internal sealed record Launch(ProcessStartInfo StartInfo, string SentinelPath, string OutsidePath);

    private sealed record ProbeResult(string Inside, string Readback, string Cleanup, string Outside, string? Detail);

    private static readonly JsonSerializerOptions ResultOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    internal static void Run(ProcessStartInfo worker, DispatchProcessHost.DispatchRunParameters parameters,
        Func<Launch, WorkerProcessRunResult>? launch)
    {
        var worktree = ResolveDirectoryPath(parameters.WorkingDirectory);
        var token = Guid.NewGuid().ToString("N");
        var sentinel = Path.Combine(worktree, $".mcg-write-probe-{token}.tmp");
        var tempPath = GetWindowsHostTempPath();
        RequireOutsideWorktree(worktree, ResolveDirectoryPath(OrchestratorTempRoot.GetRoot(tempPath)));
        if (tempPath is not null)
            RequireNonLowDirectory(tempPath);
        var scratchParent = ResolveDirectoryPath(OrchestratorTempRoot.GetPurposeDirectory("worktree-write-probe", tempPath));
        RequireOutsideWorktree(worktree, scratchParent);
        RequireNonLowDirectory(scratchParent);
        var scratch = Path.Combine(scratchParent, token);
        var outside = Path.Combine(scratch, "outside.tmp");
        var startInfo = BuildStartInfo(worker, parameters, worktree, sentinel, outside, token);

        Directory.CreateDirectory(scratch);
        Exception? failure = null;
        List<Exception> cleanupErrors = [];
        try
        {
            var request = new Launch(startInfo, sentinel, outside);
            var result = launch is null
                ? DispatchProcessHost.RunLaunchPreflightChild(request.StartInfo, "worktree write probe")
                : launch(request);
            Validate(result, request);
        }
        catch (Exception ex) when (ex is not WorkerSandboxRefusedException && ArtifactExists(sentinel))
        {
            // Capture the leftover sentinel before fallback cleanup can remove it, retaining the child failure.
            failure = new WorkerSandboxRefusedException(SentinelNotCleaned, sentinel, ex);
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            TryCleanup(sentinel, () => File.Delete(sentinel), cleanupErrors);
            TryCleanup(outside, () => File.Delete(outside), cleanupErrors);
            TryCleanup(scratch, () => Directory.Delete(scratch), cleanupErrors);
        }

        if (cleanupErrors.Count > 0)
        {
            if (failure is not null)
                cleanupErrors.Insert(0, failure);
            var detail = new AggregateException("Worktree write probe could not clean up its artifacts.", cleanupErrors);
            throw failure is WorkerSandboxRefusedException refusal
                ? new WorkerSandboxRefusedException(refusal.ReasonCode, refusal.Path, detail)
                : detail;
        }
        if (failure is not null)
            ExceptionDispatchInfo.Throw(failure);
    }

    private static void Validate(WorkerProcessRunResult result, Launch request)
    {
        if (ArtifactExists(request.OutsidePath))
            throw new WorkerSandboxRefusedException(OutsideWriteAllowed, request.OutsidePath,
                new InvalidOperationException($"Probe created the outside file. Exit={result.ExitCode}; " +
                    $"sentinel path={request.SentinelPath}; stdout={result.StandardOutput.Trim()}; stderr={result.StandardError.Trim()}"));
        if (result.TimedOut)
            throw new TimeoutException("Low-integrity launch preflight timed out running 'worktree write probe'.");
        if (result.ExitCode != 0 || result.CaptureLimited)
            throw new InvalidOperationException($"Worktree write probe failed with exit {result.ExitCode} and " +
                $"captureLimited={result.CaptureLimited}: {result.StandardError.Trim()} {result.StandardOutput.Trim()}");

        var lines = result.StandardOutput.Split('\n', StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith(ResultPrefix, StringComparison.Ordinal)).ToArray();
        var report = lines.Length == 1
            ? JsonSerializer.Deserialize<ProbeResult>(lines[0][ResultPrefix.Length..], ResultOptions)
            : null;
        var sentinelRemains = ArtifactExists(request.SentinelPath);
        if (!sentinelRemains && report is { Inside: "ok", Readback: "match", Cleanup: "removed", Outside: "denied" })
            return;

        var detail = new InvalidOperationException(
            $"Probe result: {report}; sentinel present={sentinelRemains}; sentinel path={request.SentinelPath}.");

        if (report?.Outside == "allowed")
            throw new WorkerSandboxRefusedException(OutsideWriteAllowed, request.OutsidePath, detail);
        if (report?.Inside == "denied")
            throw new WorkerSandboxRefusedException(InsideWriteDenied, request.SentinelPath, detail);
        if (sentinelRemains || report?.Cleanup == "failed")
            throw new WorkerSandboxRefusedException(SentinelNotCleaned, request.SentinelPath, detail);
        throw detail;
    }

    private static bool ArtifactExists(string path)
    {
        try
        {
            // File.Exists also returns false for missing parents and unreadable paths.
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static void TryCleanup(string path, Action delete, List<Exception> errors)
    {
        try { delete(); }
        catch (DirectoryNotFoundException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new IOException($"Could not remove probe artifact '{path}'.", ex));
        }
    }

    private static void RequireOutsideWorktree(string worktree, string scratch)
    {
        var relative = Path.GetRelativePath(worktree, scratch);
        if (!Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            throw new InvalidOperationException($"Worktree write probe scratch root '{scratch}' is within '{worktree}'.");
    }

    private static string? GetWindowsHostTempPath()
    {
        if (!OperatingSystem.IsWindows())
            return null;

        // The process TEMP can point to a Low-integrity worker or test directory.
        // Acceptance preserves the real LOCALAPPDATA even when it redirects USERPROFILE.
        var localAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        if (localAppData is null || !Path.IsPathFullyQualified(localAppData))
            localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!Path.IsPathFullyQualified(localAppData))
            throw new InvalidOperationException("Worktree write probe could not resolve the host's Windows temp directory.");

        return ResolveDirectoryPath(Path.Combine(localAppData, "Temp"));
    }

    private static void RequireNonLowDirectory(string path)
    {
        if (new IcaclsIntegrityLabeler().Query(path).Low)
            throw new InvalidOperationException($"Worktree write probe cannot use Low-integrity scratch directory '{path}'.");
    }

    private static string ResolveDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath)!;
        var current = root;
        foreach (var part in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            var directory = new DirectoryInfo(Path.Combine(current, part));
            current = directory.LinkTarget is null
                ? directory.FullName
                : directory.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new IOException($"Could not resolve probe directory '{directory.FullName}'.");
        }
        return current;
    }

    private static ProcessStartInfo BuildStartInfo(ProcessStartInfo worker, DispatchProcessHost.DispatchRunParameters parameters,
        string worktree, string sentinel, string outside, string payload)
    {
        static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        var dropScript = Path.Combine(DispatchProcessHost.ResolveSandboxRoot(parameters), "drop-to-low.ps1");
        var command = $$"""
            $ErrorActionPreference = 'Stop'
            . {{Quote(dropScript)}}
            $sentinel = {{Quote(sentinel)}}
            $outside = {{Quote(outside)}}
            $payload = {{Quote(payload)}}
            $result = @{ inside = 'error'; readback = 'skipped'; cleanup = 'skipped'; outside = 'error' }
            $detail = @()
            $created = $false
            try {
                $stream = [IO.File]::Open($sentinel, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $created = $true
                try {
                    $bytes = [Text.Encoding]::UTF8.GetBytes($payload)
                    $stream.Write($bytes, 0, $bytes.Length)
                } finally {
                    $stream.Dispose()
                }
                $result.inside = 'ok'
                $result.readback = if ([IO.File]::ReadAllText($sentinel) -ceq $payload) { 'match' } else { 'mismatch' }
            } catch [UnauthorizedAccessException] {
                if ($result.inside -ne 'ok') { $result.inside = 'denied' }
                $detail += $_.Exception.Message
            } catch {
                $detail += $_.Exception.Message
            } finally {
                if ($created) {
                    try {
                        [IO.File]::Delete($sentinel)
                        $result.cleanup = 'removed'
                    } catch {
                        $result.cleanup = 'failed'
                        $detail += $_.Exception.Message
                    }
                }
            }
            if ([IO.Directory]::Exists([IO.Path]::GetDirectoryName($outside))) {
                try {
                    $stream = [IO.File]::Open($outside, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                    $result.outside = 'allowed'
                    $stream.Dispose()
                } catch [UnauthorizedAccessException] {
                    if ($result.outside -ne 'allowed') { $result.outside = 'denied' }
                    $detail += $_.Exception.Message
                } catch {
                    $detail += $_.Exception.Message
                }
            } else {
                $detail += 'Outside scratch directory is missing or inaccessible.'
            }
            $result.detail = $detail -join ' | '
            [Console]::WriteLine('{{ResultPrefix}}' + ($result | ConvertTo-Json -Compress))
            exit 0
            """;
        var startInfo = WorkerProcessRunner.BuildPowerShellStartInfo(command, worktree, redirectStandardInput: false);
        startInfo.Environment.Clear();
        foreach (var entry in worker.Environment)
            startInfo.Environment[entry.Key] = entry.Value;
        startInfo.Environment.Remove(WorkerSandboxOptions.DispatchWorkerVariable);
        return startInfo;
    }
}
