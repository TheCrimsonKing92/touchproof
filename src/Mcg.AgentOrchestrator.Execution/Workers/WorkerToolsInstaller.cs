using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed class WorkerToolsInstaller(string configurationPath, string cacheRoot)
{
    private const int GitTimeoutMilliseconds = 30_000;
    private static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(3);
    private static readonly string CommandName = OperatingSystem.IsWindows() ? "wt.exe" : "wt";
    private static readonly JsonSerializerOptions ConfigJson = new() { PropertyNameCaseInsensitive = true };

    public string? ResolveForDispatch(AgentOrchestratorKernel kernel, GoalId goalId, TaskSpec task) =>
        WorkerToolsPromptSection.AppliesTo(task.RequiredRole)
            ? Resolve(message => kernel.RecordTaskNote(goalId, task.Id, message))
            : null;

    private string? Resolve(Action<string> reportFailure)
    {
        var stage = "configuration";
        var pin = "unconfigured";
        string? commit = null;
        try
        {
            var settings = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(configurationPath), ConfigJson)
                ?? throw new InvalidDataException("Worker tools configuration is empty.");
            pin = settings.PinnedCommit ?? pin;
            if (pin.Length is < 4 or > 64 || !pin.All(Uri.IsHexDigit))
                throw new InvalidDataException("pinnedCommit must be a hexadecimal Git commit identity.");
            var source = Path.GetFullPath(settings.SourceRepository
                ?? throw new InvalidDataException("sourceRepository is required."), Path.GetDirectoryName(configurationPath)!);
            var project = settings.Project ?? throw new InvalidDataException("project is required.");
            stage = "resolve";
            var identities = RunGit(source, "rev-parse", $"--disambiguate={pin}")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (identities is not [var resolved] || RunGit(source, "cat-file", "-t", resolved).Trim() != "commit")
                throw new InvalidDataException("The pin must identify exactly one commit in the configured repository.");
            commit = resolved;
            stage = "cache";
            var entry = Path.Combine(cacheRoot, $"{commit}-{RuntimeInformation.RuntimeIdentifier}");
            var command = Path.Combine(entry, CommandName);
            if (File.Exists(command))
                return entry;
            Directory.CreateDirectory(cacheRoot);
            using var lease = AcquireLease(entry + ".lock");
            if (File.Exists(command))
                return entry;

            // Keep both the source snapshot and every build product off the two worktrees.
            var staging = Path.Combine(cacheRoot, $".install-{Guid.NewGuid():N}");
            try
            {
                Directory.CreateDirectory(staging);
                var sourceDirectory = Path.Combine(staging, "source");
                var archive = Path.Combine(staging, "snapshot.zip");
                stage = "materialize";
                RunGit(source, "archive", "--format=zip", $"--output={archive}", commit);
                ZipFile.ExtractToDirectory(archive, sourceDirectory);
                var projectPath = Path.GetFullPath(project, sourceDirectory);
                var relative = Path.GetRelativePath(sourceDirectory, projectPath);
                if (Path.IsPathRooted(relative) || relative == ".." ||
                    relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(projectPath))
                    throw new InvalidDataException($"Project '{project}' was not found inside the pinned snapshot.");

                stage = "publish";
                var output = Path.Combine(staging, "publish");
                Publish(sourceDirectory, projectPath, output);
                if (!File.Exists(Path.Combine(output, CommandName)))
                    throw new InvalidDataException($"The publish output did not contain {CommandName}.");
                stage = "install";
                if (Directory.Exists(entry))
                    Directory.Delete(entry, recursive: true);
                Directory.Move(output, entry);
                return entry;
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException
            or ArgumentException or Win32Exception or TimeoutException)
        {
            RecordFailure(commit ?? pin.ToLowerInvariant(),
                $"WORKER_TOOLS_INSTALL_FAILED pin={pin} commit={commit ?? "unresolved"} stage={stage} error={ex.Message}", reportFailure);
            return null;
        }
    }

    private void RecordFailure(string identity, string message, Action<string> reportFailure)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var directory = Path.Combine(cacheRoot, "diagnostics");
        var path = Path.Combine(directory, key + ".txt");
        try
        {
            Directory.CreateDirectory(directory);
            using var lease = AcquireLease(path + ".lock");
            if (File.Exists(path))
                return;
            File.WriteAllText(path, message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or TimeoutException)
        {
            reportFailure($"{message} (diagnostic persistence failed: {ex.Message})");
            return;
        }
        reportFailure(message);
    }

    private static FileStream AcquireLease(string path)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            try { return File.Open(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException ex) when ((ex.HResult & 0xffff) is 11 or 32 or 33)
            {
                if (deadline.Elapsed > BuildTimeout + TimeSpan.FromMinutes(1))
                    throw new TimeoutException($"Timed out waiting for worker tools cache entry '{path}'.", ex);
                Thread.Sleep(100);
            }
        }
    }

    private static string RunGit(string source, params string[] args)
    {
        var result = GitCli.RunForRepository(source, GitTimeoutMilliseconds, args);
        return result.Succeeded && !result.DrainTimedOut ? result.Output
            : throw new IOException($"Git {args[0]} failed in '{source}': {result.Error.Trim()}");
    }

    private static void Publish(string source, string project, string output)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = source,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "publish", project, "--configuration", "Release", "--output", output,
                     "--self-contained", "false", "-p:UseAppHost=true", "-p:UseSharedCompilation=false", "--nologo" })
            start.ArgumentList.Add(argument);
        start.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        start.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = HandleListProcessLauncher.StartSuppressed(start);
        using var timeout = new CancellationTokenSource(BuildTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"dotnet publish exceeded {BuildTimeout}.");
        }
        if (process.ExitCode != 0)
        {
            var details = string.Join(Environment.NewLine, stdout.Result.Trim(), stderr.Result.Trim()).Trim();
            throw new IOException($"dotnet publish exited {process.ExitCode}: {details[^Math.Min(details.Length, 4000)..]}");
        }
    }

    private sealed record Configuration(string? PinnedCommit, string? SourceRepository, string? Project);
}
