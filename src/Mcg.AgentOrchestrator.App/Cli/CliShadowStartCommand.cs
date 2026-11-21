using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliShadowStartCommand
{
    internal const string Name = "shadow-start";
    internal const string NoDispatchFlag = "--no-dispatch";
    private const string StateDirectoryName = ".orchestrator";
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan CleanupBudget = TimeSpan.FromSeconds(5);

    internal static bool IsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals(Name, StringComparison.OrdinalIgnoreCase);

    internal static bool IsNoDispatch(IReadOnlyList<string> args) =>
        args.Count > 0 && args[0].Equals("conduct", StringComparison.OrdinalIgnoreCase) &&
        args.Any(arg => arg.Equals(NoDispatchFlag, StringComparison.OrdinalIgnoreCase));

    internal static bool Execute(IReadOnlyList<string> args)
    {
        if (Run(args, Console.Out, Console.Error) != 0)
            throw new InvalidOperationException("Shadow startup check failed.");
        return false;
    }

    internal static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr) =>
        RunAsync(args, stdout, stderr).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        var assertion = "arguments";
        string? scratch = null;
        try
        {
            var options = Parse(args);
            assertion = "scratch-containment";
            var source = ResolveDirectoryPath(options.Source);
            scratch = ResolveDirectoryPath(options.Scratch);
            if (!Directory.Exists(source))
                throw new IOException($"Source state directory does not exist: {source}");
            var relative = Path.GetRelativePath(source, scratch);
            if (relative == "." || (!Path.IsPathRooted(relative) && relative != ".." &&
                !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)))
                throw new IOException("Scratch must be outside the source state directory.");
            if (EntryExists(options.Scratch) || EntryExists(scratch))
                throw new IOException("Scratch directory must be new.");

            assertion = "database-backup";
            CreateScratch(scratch);
            var state = Directory.CreateDirectory(Path.Combine(scratch, StateDirectoryName)).FullName;
            CopyStateDatabases(source, state);
            assertion = "candidate-launch";
            return await SuperviseAsync(options, scratch, state, stdout, stderr).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or
            InvalidOperationException or SqliteException or Win32Exception or NotSupportedException)
        {
            Report(stderr, assertion, ex.Message, scratch, null, null, null);
            return 1;
        }
    }

    private static Options Parse(IReadOnlyList<string> args)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var candidateArguments = new List<string>();
        for (var i = IsCommand(args) ? 1 : 0; i < args.Count; i++)
        {
            var arg = args[i];
            var equals = arg.IndexOf('=');
            var flag = (equals < 0 ? arg : arg[..equals]).ToLowerInvariant();
            if (flag is not ("--source" or "--scratch" or "--candidate" or "--candidate-arg" or
                "--startup-ceiling-seconds" or "--budget-seconds"))
                throw new ArgumentException($"Unknown shadow-start argument: {arg}");
            var value = equals >= 0 ? arg[(equals + 1)..]
                : ++i < args.Count && !args[i].StartsWith("--", StringComparison.Ordinal) ? args[i]
                : throw new ArgumentException($"Missing value for {flag}.");
            if (flag == "--candidate-arg")
                candidateArguments.Add(value);
            else if (string.IsNullOrWhiteSpace(value) || !values.TryAdd(flag, value))
                throw new ArgumentException($"Empty or duplicate value for {flag}.");
        }

        var startup = Seconds("--startup-ceiling-seconds", 60);
        var budget = Seconds("--budget-seconds", 120);
        if (budget <= startup)
            throw new ArgumentException("--budget-seconds must exceed --startup-ceiling-seconds.");
        var candidate = values.GetValueOrDefault("--candidate");
        if (candidate is null && candidateArguments.Count > 0)
            throw new ArgumentException("--candidate-arg requires --candidate.");
        return new(Absolute("--source"), Absolute("--scratch"), candidate, candidateArguments, startup, budget);

        string Absolute(string flag) =>
            values.TryGetValue(flag, out var path) && Path.IsPathFullyQualified(path)
                ? WorkspaceInvocationBinding.CanonicalPath(path)
                : throw new ArgumentException($"{flag} requires an absolute path.");

        TimeSpan Seconds(string flag, double fallback)
        {
            if (!values.TryGetValue(flag, out var value))
                return TimeSpan.FromSeconds(fallback);
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds) ||
                !double.IsFinite(seconds) || seconds <= 0 || seconds >= TimeSpan.MaxValue.TotalSeconds)
                throw new ArgumentException($"{flag} requires a positive, finite duration in seconds.");
            var duration = TimeSpan.FromSeconds(seconds);
            return duration > TimeSpan.Zero ? duration : throw new ArgumentException($"{flag} is too small.");
        }
    }

    private static string ResolveDirectoryPath(string path, int remainingLinks = 40)
    {
        if (remainingLinks == 0 || (OperatingSystem.IsWindows() &&
            (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))))
            throw new IOException($"Cannot establish physical directory containment for {path}.");
        var parent = Path.GetDirectoryName(path);
        if (parent is not null)
            path = Path.Combine(ResolveDirectoryPath(parent, remainingLinks), Path.GetFileName(path));
        FileAttributes attributes;
        try { attributes = File.GetAttributes(path); }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            if (parent is null)
                throw new IOException($"Cannot resolve the filesystem root of {path}.", ex);
            if (new DirectoryInfo(path).LinkTarget is not null)
                throw new IOException($"Cannot resolve directory alias {path}.", ex);
            return path;
        }
        if (attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            var target = Directory.ResolveLinkTarget(path, returnFinalTarget: false) ??
                throw new IOException($"Cannot resolve directory alias {path}.");
            if (!target.Exists)
                throw new IOException($"Cannot resolve directory alias {path}.");
            return ResolveDirectoryPath(WorkspaceInvocationBinding.CanonicalPath(target.FullName), remainingLinks - 1);
        }
        if (!attributes.HasFlag(FileAttributes.Directory))
            throw new IOException($"Not a directory: {path}.");
        return path;
    }

    private static bool EntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { return false; }
    }

    private static void CreateScratch(string scratch)
    {
        var parent = Directory.CreateDirectory(Path.GetDirectoryName(scratch)!).FullName;
        var reservation = Directory.CreateDirectory(Path.Combine(parent, $".shadow-start-{Guid.NewGuid():N}")).FullName;
        try { Directory.Move(reservation, scratch); }
        finally { if (Directory.Exists(reservation)) Directory.Delete(reservation); }
    }

    private static void CopyStateDatabases(string source, string state)
    {
        foreach (var database in StoreSchemaRegistry.Inventory.Select(entry => entry.Database)
            .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (database is "per-database" or "impact-replay.checkpoint.db")
                continue;
            var sourcePath = Path.Combine(source, database);
            if (!File.Exists(sourcePath))
                continue;

            using var live = OpenDatabase(sourcePath, SqliteOpenMode.ReadOnly);
            using var copy = OpenDatabase(Path.Combine(state, database), SqliteOpenMode.ReadWriteCreate);
            live.BackupDatabase(copy);
        }
    }

    private static SqliteConnection OpenDatabase(string path, SqliteOpenMode mode)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path, Mode = mode, Pooling = false
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    private static ProcessStartInfo BuildLaunch(Options options, string scratch, string state)
    {
        var info = new ProcessStartInfo(options.Candidate ?? "dotnet")
        {
            WorkingDirectory = scratch, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        if (options.Candidate is null)
            info.ArgumentList.Add(typeof(CliShadowStartCommand).Assembly.Location);
        foreach (var arg in options.CandidateArguments)
            info.ArgumentList.Add(arg);
        foreach (var arg in new[] { "conduct", "--loop", "--daemon", NoDispatchFlag, "--poll-seconds", "1",
            "--repository-root", scratch, "--expected-state-directory", state, "--disposable-fixture" })
            info.ArgumentList.Add(arg);

        foreach (var key in info.Environment.Keys.Where(key =>
            key.StartsWith("MCG_", StringComparison.OrdinalIgnoreCase) ||
            key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            info.Environment.Remove(key);
        info.Environment[OrchestratorWorkspace.RepoRootEnvironmentVariable] = scratch;
        info.Environment[OrchestratorDataRoot.EnvironmentVariable] = Path.Combine(scratch, "data");
        info.Environment[OrchestratorProjectRegistry.RegistryHomeEnvironmentVariable] = Path.Combine(scratch, "projects");
        info.Environment[OrchestratorHome.EnvironmentVariable] = scratch;
        info.Environment[DotnetBuildStorageLayout.IsolatedRootOverrideVariable] = Path.Combine(scratch, "builds");
        var temp = Directory.CreateDirectory(Path.Combine(scratch, "temp")).FullName;
        foreach (var key in new[] { "TEMP", "TMP", "TMPDIR" })
            info.Environment[key] = temp;
        info.Environment["GIT_CEILING_DIRECTORIES"] = Path.GetDirectoryName(scratch)!;
        return info;
    }

    private static async Task<int> SuperviseAsync(Options options, string scratch, string state,
        TextWriter stdout, TextWriter stderr)
    {
        var info = BuildLaunch(options, scratch, state);
        var logs = Path.Combine(state, "logs");
        var stopFile = Path.Combine(scratch, ConductorBatchLoop.StopFileName);
        var tail = new Queue<string>();
        var evidence = new JournalEvidence();
        DateTimeOffset? stopRequested = null;
        long? peak = null;
        string? failure = null;
        string? detail = null;

        using var group = OwnedProcessGroup.CreateContained();
        var launchedAt = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        using var child = group.StartRedirected(info);
        using var process = TryAttach(child);
        child.StandardInput.Close();
        var output = DrainAsync(child.StandardOutput, "stdout");
        var error = DrainAsync(child.StandardError, "stderr");
        try
        {
            while (true)
            {
                if (TryReadPeak(process) is { } sample)
                    peak = Math.Max(peak ?? 0, sample);
                var exited = child.HasExited;
                if (exited)
                    elapsed.Stop();
                evidence.Observe(logs, launchedAt, stopRequested);
                if (elapsed.Elapsed >= options.Budget)
                    failure = "wall-clock-budget";
                else if (evidence.FirstTick is { } firstTick && firstTick - launchedAt >= options.StartupCeiling)
                    failure = "startup-ceiling";
                else if (exited)
                {
                    (failure, detail) = !evidence.Started ? ("loop-start", "startup failure: child exited before loop-start")
                        : evidence.FirstTick is null ? ("startup-first-tick", "startup failure: child exited before first tick")
                        : stopRequested is null ? ("tick-advance", "child exited before the supervisor requested stop")
                        : child.ExitCode != 0 ? ("exit-code", $"child exited with code {child.ExitCode}")
                        : !evidence.Stopped ? ("stop-file-loop-stop", "missing clean loop-stop after the stop request")
                        : (null, null);
                    break;
                }
                else if (evidence.FirstTick is null && elapsed.Elapsed >= options.StartupCeiling)
                    failure = "startup-ceiling";
                if (failure is not null)
                    break;

                if (stopRequested is null && evidence.Started && evidence.FirstTick is not null && evidence.Advanced)
                {
                    stopRequested = DateTimeOffset.UtcNow;
                    using var stop = new FileStream(stopFile, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                }

                var deadline = evidence.FirstTick is null ? options.StartupCeiling : options.Budget;
                var remaining = deadline - elapsed.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining < PollInterval ? remaining : PollInterval).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or Win32Exception)
        {
            failure = "candidate-supervision";
            detail = ex.Message;
        }
        finally
        {
            var confirmed = OperatingSystem.IsWindows()
                ? group.TerminateAndConfirmEmpty(CleanupBudget)
                : StopPortableGroup(group, child);
            if (elapsed.IsRunning)
                elapsed.Stop();
            if (!confirmed)
            {
                failure ??= "candidate-cleanup";
                detail = $"{detail} Child process-tree termination could not be confirmed.".Trim();
            }
        }

        try { await Task.WhenAll(output, error).WaitAsync(CleanupBudget).ConfigureAwait(false); }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            failure ??= "candidate-output";
            detail = $"{detail} Child output drain failed: {ex.Message}".Trim();
        }

        var resultWriter = failure is null ? stdout : stderr;
        Report(resultWriter, failure, detail, scratch, elapsed.ElapsedMilliseconds, peak,
            evidence.FirstTick is { } observed ? (long)(observed - launchedAt).TotalMilliseconds : null);
        if (failure is not null)
        {
            lock (tail)
                foreach (var line in tail)
                    stderr.WriteLine(line);
        }
        return failure is null ? 0 : 1;

        async Task DrainAsync(StreamReader stream, string channel)
        {
            while (await stream.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                lock (tail)
                {
                    if (tail.Count == 40) tail.Dequeue();
                    tail.Enqueue($"{channel}: {(line.Length <= 2048 ? line : line[..2048])}");
                }
            }
        }
    }

    private static Process? TryAttach(HandleListLaunchedProcess child)
    {
        try { return child.AttachProcess(); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    private static long? TryReadPeak(Process? process)
    {
        if (process is null)
            return null;
        try { process.Refresh(); return process.PeakWorkingSet64; }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception) { return null; }
    }

    private static bool StopPortableGroup(OwnedProcessGroup group, HandleListLaunchedProcess child)
    {
        group.Kill();
        return child.WaitForExit(CleanupBudget);
    }

    private static void Report(TextWriter writer, string? assertion, string? detail, string? scratch,
        long? wall, long? peak, long? firstTick)
    {
        static string Measurement(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        writer.WriteLine($"SHADOW_START result={(assertion is null ? "pass" : "fail")} assertion={assertion ?? "none"} " +
            $"wall_ms={Measurement(wall)} peak_working_set_bytes={Measurement(peak)} " +
            $"first_tick_ms={Measurement(firstTick)} scratch={scratch ?? "unavailable"}");
        if (assertion is not null)
            writer.WriteLine(detail ?? $"Shadow startup assertion failed: {assertion}.");
    }

    private sealed record Options(string Source, string Scratch, string? Candidate,
        IReadOnlyList<string> CandidateArguments, TimeSpan StartupCeiling, TimeSpan Budget);

    private sealed class JournalEvidence
    {
        internal bool Started { get; private set; }
        internal DateTimeOffset? FirstTick { get; private set; }
        internal bool Advanced { get; private set; }
        internal bool Stopped { get; private set; }

        internal void Observe(string logs, DateTimeOffset launch, DateTimeOffset? stop)
        {
            if (!Directory.Exists(logs))
                return;
            foreach (var file in Directory.EnumerateFiles(logs, "conduct-events*.log"))
            {
                try
                {
                    using var stream = new FileStream(file, FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(stream);
                    while (reader.ReadLine() is { } line)
                        ObserveLine(line, launch, stop);
                }
                catch (FileNotFoundException) { /* The journal rotated before this file was opened. */ }
            }
        }

        private void ObserveLine(string line, DateTimeOffset launch, DateTimeOffset? stop)
        {
            line = line.TrimStart('\uFEFF');
            if (!line.StartsWith('{'))
                return;
            try
            {
                using var json = JsonDocument.Parse(line);
                var record = json.RootElement;
                if (!record.TryGetProperty("timestamp", out var timestamp) ||
                    timestamp.ValueKind != JsonValueKind.String || !timestamp.TryGetDateTimeOffset(out var at) || at < launch ||
                    !record.TryGetProperty("eventKind", out var kind) || kind.ValueKind != JsonValueKind.String)
                    return;

                var detail = record.TryGetProperty("detail", out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()! : string.Empty;
                switch (kind.GetString())
                {
                    case "loop-start":
                        Started = true;
                        break;
                    case "startup-first-tick":
                    case "loop-first-tick":
                        if (FirstTick is null || at < FirstTick)
                            FirstTick = at;
                        break;
                    case "tick":
                        Advanced |= detail.Split(' ').Any(token => token.StartsWith("tick=", StringComparison.Ordinal) &&
                            int.TryParse(token.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var tick) && tick >= 2);
                        break;
                    case "loop-stop" when stop is { } requested && at >= requested:
                        Stopped |= detail.Split(' ').Any(token => token is "reason=stop-file" or
                            "reason=stop-file-during-sleep" or "reason=stop-while-idle");
                        break;
                }
            }
            catch (JsonException) { /* Ignore an incomplete or unstructured journal line. */ }
        }
    }
}
