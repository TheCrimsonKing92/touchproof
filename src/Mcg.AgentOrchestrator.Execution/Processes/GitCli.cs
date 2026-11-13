using System.Diagnostics;
using Microsoft.Win32.SafeHandles;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class GitCli
{
    internal static Action<string>? LaunchObserver;
    internal static Func<string, Action<GitProcessTiming>?>? CompletionObserver;
    private static GitIdleGapHistory idleGaps = new();

    internal static void BindIdleGapHistory(string stateDirectory) =>
        Volatile.Write(ref idleGaps, new GitIdleGapHistory(Path.Combine(stateDirectory, GitIdleGapHistory.FileName)));

    internal readonly record struct GitProcessTiming(string Subcommand, double StartMs, double WallMs, double? CpuMs);

    // One instance per observed process. The sink is selected on the launching thread and retained
    // through exit, so a later observer replacement cannot redirect an in-flight measurement.
    internal sealed class ProcessCompletion
    {
        private readonly Func<string, Action<GitProcessTiming>?> observer;
        private readonly string subcommand;
        private readonly TimeProvider clock;
        private readonly long beforeStart;
        private Action<GitProcessTiming>? sink;
        private double startMs;
        private int completed;

        private ProcessCompletion(Func<string, Action<GitProcessTiming>?> observer, string subcommand,
            TimeProvider clock)
        {
            this.observer = observer;
            this.subcommand = subcommand;
            this.clock = clock;
            beforeStart = clock.GetTimestamp();
        }

        internal static ProcessCompletion? Begin(Func<string, Action<GitProcessTiming>?>? observer,
            string subcommand, TimeProvider clock)
        {
            if (observer is null) return null;
            try { return new(observer, subcommand, clock); }
            catch (Exception error) { Diagnose(error); return null; }
        }

        internal void Started()
        {
            try
            {
                startMs = clock.GetElapsedTime(beforeStart, clock.GetTimestamp()).TotalMilliseconds;
                sink = observer(subcommand);
            }
            catch (Exception error) { Diagnose(error); }
        }

        internal void CompleteIfExited(Process process)
        {
            try { if (process.HasExited) Complete(process); }
            catch (Exception error) { Diagnose(error); }
        }

        internal void Complete(Process process) => Complete(() => process.TotalProcessorTime);

        internal void CompleteIfExited(HandleListLaunchedProcess process)
        {
            try { if (process.HasExited) Complete(process); }
            catch (Exception error) { Diagnose(error); }
        }

        internal void Complete(HandleListLaunchedProcess process) => Complete(() => process.TotalProcessorTime);

        private void Complete(Func<TimeSpan> cpuTime)
        {
            if (Interlocked.Exchange(ref completed, 1) != 0 || sink is null) return;
            try
            {
                var wallMs = clock.GetElapsedTime(beforeStart, clock.GetTimestamp()).TotalMilliseconds;
                double? cpuMs = null;
                try
                {
                    var value = cpuTime().TotalMilliseconds;
                    if (double.IsFinite(value) && value >= 0) cpuMs = value;
                    else Diagnose(new InvalidOperationException("git processor time is not finite and non-negative"));
                }
                catch (Exception error) { Diagnose(error); }
                sink(new(subcommand, startMs, wallMs, cpuMs));
            }
            catch (Exception error) { Diagnose(error); }
        }

        private static void Diagnose(Exception error)
        {
            try { Console.Error.WriteLine($"Git completion telemetry: {error.GetType().Name}: {error.Message}"); }
            catch (Exception) { /* Telemetry must never change the command result. */ }
        }
    }

    [ThreadStatic]
    internal static Action<ProcessStartInfo>? StartInfoObserver;

    // 60 000 ms covers the longest observed per-call timeout in use across the codebase.
    internal const int DefaultTimeoutMilliseconds = 60_000;

    // After git itself exits, bound how long we wait for stdout/stderr to drain. A git background
    // helper (fsmonitor/maintenance/gc) can inherit the output pipe and hold it open after git
    // exits; a synchronous ReadToEnd would then block forever and DEFEAT the timeout below (observed:
    // an inherited-pipe git wedged the conductor for 34 minutes while holding index.lock).
    private const int DrainTimeoutMilliseconds = PipeDrain.DefaultTimeoutMilliseconds;

    // Applied to every invocation so git fails fast and never wedges:
    //  - core.fsmonitor=false / gc.auto=0 / maintenance.auto=false: never spawn a background daemon
    //    that could inherit the output pipe or keep a lock alive after git returns.
    //  - core.longpaths=true: keep revision/path arguments usable from deeply nested fixture worktrees.
    public static IReadOnlyList<string> HardeningArguments { get; } = Array.AsReadOnly<string>(
    [
        "-c", "core.fsmonitor=false",
        "-c", "core.longpaths=true",
        "-c", "gc.auto=0",
        "-c", "maintenance.auto=false",
    ]);

    // Apply once, after caller arguments and environment, immediately before launch.
    internal static bool ApplyLaunchSettings(ProcessStartInfo startInfo, bool readCommand)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        for (var index = 0; index < HardeningArguments.Count; index++)
            startInfo.ArgumentList.Insert(index, HardeningArguments[index]);
        startInfo.CreateNoWindow = true;
        startInfo.UseShellExecute = false;
        if (readCommand) startInfo.Environment["GIT_OPTIONAL_LOCKS"] = "0";
        var useConsolePolicy = GitExecutable.Prepare(startInfo);

        try { StartInfoObserver?.Invoke(startInfo); }
        catch (Exception) { /* Launch telemetry must not change the git result. */ }
        return useConsolePolicy;
    }

    public readonly record struct GitResult(
        int ExitCode,
        string Output,
        string Error,
        bool DrainTimedOut = false,
        bool ProcessStarted = true)
    {
        public bool Succeeded => ExitCode == 0;
    }

    internal readonly record struct WorktreeStatusInspection(
        bool Succeeded,
        IReadOnlyList<string> CommitWorthyPaths,
        string? Error)
    {
        public bool IsDirty => CommitWorthyPaths.Count > 0;
    }

    internal readonly record struct AheadBehindInspection(
        bool Succeeded,
        int? Ahead,
        int? Behind,
        string? Error);

    public static GitResult Run(string workingDirectory, params string[] args) =>
        RunExecutableCore("git", workingDirectory, null, null, args,
            Volatile.Read(ref CompletionObserver), TimeProvider.System);

    public static GitResult Run(string workingDirectory, int timeoutMilliseconds, params string[] args)
        => RunExecutable("git", workingDirectory, timeoutMilliseconds, args);

    internal static GitResult RunForRepository(string workingDirectory, params string[] args) =>
        RunExecutableCore("git", workingDirectory, null, null, args,
            Volatile.Read(ref CompletionObserver), TimeProvider.System,
            isolateRepository: true);

    internal static GitResult RunWithEnvironment(
        string workingDirectory,
        IReadOnlyDictionary<string, string> environment,
        params string[] args) =>
        RunExecutableCore("git", workingDirectory, null, null, args,
            Volatile.Read(ref CompletionObserver), TimeProvider.System, environment);

    public static GitResult RunWithStandardInput(
        string workingDirectory, int timeoutMilliseconds, string standardInput, params string[] args) =>
        RunExecutableCore("git", workingDirectory, timeoutMilliseconds, standardInput, args,
            Volatile.Read(ref CompletionObserver), TimeProvider.System);

    internal static GitResult RunExecutable(
        string executable,
        string workingDirectory,
        int timeoutMilliseconds,
        params string[] args)
        => RunExecutableCore(executable, workingDirectory, timeoutMilliseconds, null, args,
            Volatile.Read(ref CompletionObserver), TimeProvider.System);

    // Explicit observer and clock keep tests independent of the process-global census.
    internal static GitResult RunExecutableObserved(string executable, string workingDirectory,
        int timeoutMilliseconds, Func<string, Action<GitProcessTiming>?>? completionObserver,
        TimeProvider clock, params string[] args) =>
        RunExecutableCore(executable, workingDirectory, timeoutMilliseconds, null, args, completionObserver, clock);

    // Instance-scoped observation for coordinated launch-window tests; no ambient hook.
    internal static GitResult RunExecutable(
        string executable, string workingDirectory, int timeoutMilliseconds, string[] args,
        ProcessTreeGuiSuppression.ErrorModeAccess? errorModeAccess, Action? lockContended,
        Action<HandleListLaunchedProcess>? processStarted = null)
        => RunExecutableCore(executable, workingDirectory, timeoutMilliseconds, null, args,
            Volatile.Read(ref CompletionObserver), TimeProvider.System,
            errorModeAccess: errorModeAccess, lockContended: lockContended, startedObserver: processStarted);

    private static GitResult RunExecutableCore(
        string executable,
        string workingDirectory,
        int? timeoutMilliseconds,
        string? standardInput,
        string[] args,
        Func<string, Action<GitProcessTiming>?>? completionObserver,
        TimeProvider clock,
        IReadOnlyDictionary<string, string>? environment = null,
        bool isolateRepository = false,
        ProcessTreeGuiSuppression.ErrorModeAccess? errorModeAccess = null,
        Action? lockContended = null, Action<HandleListLaunchedProcess>? startedObserver = null,
        GitIdleGapHistory? idleGapHistory = null, Action<GitProgressWatch.Observation>? sampled = null,
        Action<ProcessStartInfo>? configureStart = null)
    {
        var processStarted = false;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = standardInput is not null,
                WorkingDirectory = workingDirectory
            };
            foreach (var arg in args)
                startInfo.ArgumentList.Add(arg);

            if (isolateRepository)
            {
                foreach (var name in new[] { "GIT_DIR", "GIT_WORK_TREE", "GIT_COMMON_DIR", "GIT_INDEX_FILE",
                    "GIT_OBJECT_DIRECTORY", "GIT_ALTERNATE_OBJECT_DIRECTORIES" })
                    startInfo.Environment.Remove(name);
                startInfo.Environment["GIT_TERMINAL_PROMPT"] = "0";
            }

            if (environment is not null)
            {
                foreach (var (key, value) in environment)
                    startInfo.Environment[key] = value;
            }

            configureStart?.Invoke(startInfo);
            // Read-only commands (status/diff/log) skip the index.lock, so recovery inspection can
            // never take or block on it; write commands take their required locks regardless.
            var useConsolePolicy = ApplyLaunchSettings(startInfo, readCommand: true);

            var history = idleGapHistory ?? Volatile.Read(ref idleGaps);
            var subcommand = SubcommandKey(args);
            var stallWindow = timeoutMilliseconds is not null ? null : history.Window(subcommand);
            using var job = timeoutMilliseconds is null ? CreateAccountingJob() : null;
            var completion = ProcessCompletion.Begin(completionObserver, args.Length > 0 ? args[0] : string.Empty, clock);
            HandleListLaunchedProcess? launchedProcess;
            using (LaunchLockTelemetry.Attribution.IsActive ? LaunchLockSiteCapture.BeginGit(args) : null)
                launchedProcess = useConsolePolicy
                    ? HandleListProcessLauncher.StartSuppressed(startInfo,
                        entry: LaunchLockEntryPoint.GitCli, errorModeAccess: errorModeAccess, job: job)
                    : HandleListProcessLauncher.Start(startInfo, LaunchLockEntryPoint.GitCli, errorModeAccess, job);
            using var process = launchedProcess;
            if (process is null)
                return new GitResult(1, string.Empty, "failed to start git process", ProcessStarted: false);
            processStarted = true;
            completion?.Started();
            var observer = LaunchObserver;
            if (observer is not null) NotifyLaunch(observer, args);

            // Drain on dedicated threads, never the thread pool. Under parallel test load the pool can be
            // fully blocked; a pool-dependent async read then never starts inside the drain window and
            // the output already sitting in the pipe is discarded as "" next to a real exit code — the
            // "git exits 0 with no output" flake family (proven by GitCliThreadPoolSaturationTests).
            // Dedicated threads also keep WaitForExit's timeout real: a synchronous ReadToEnd on this
            // thread would block on an inherited pipe after git exits.
            var outputDrain = PipeDrain.Start(process.StandardOutput.BaseStream,
                process.StandardOutput.CurrentEncoding, "git-stdout-drain");
            var errorDrain = PipeDrain.Start(process.StandardError.BaseStream,
                process.StandardError.CurrentEncoding, "git-stderr-drain");
            startedObserver?.Invoke(process);

            // Write while both output pipes drain: git may emit diagnostics before consuming stdin.
            var inputWrite = standardInput is null ? null : Task.Run(() =>
            {
                process.StandardInput.Write(standardInput);
                process.StandardInput.Close();
            });

            if (timeoutMilliseconds is { } timeout)
            {
                bool inputCompleted;
                try { inputCompleted = inputWrite?.Wait(timeout) ?? true; }
                catch (AggregateException ex)
                {
                    TryKillTree(process);
                    completion?.CompleteIfExited(process);
                    return new GitResult(1, outputDrain.Text, ex.GetBaseException().Message);
                }
                if (!inputCompleted)
                {
                    TryKillTree(process);
                    completion?.CompleteIfExited(process);
                    return new GitResult(-1, outputDrain.Text, "git stdin write timed out");
                }

                if (!process.WaitForExit(timeout))
                {
                    TryKillTree(process);
                    completion?.CompleteIfExited(process);
                    return new GitResult(-1, outputDrain.Text, $"git {string.Join(' ', args)} timed out after {timeoutMilliseconds}ms");
                }
            }
            else
            {
                var watched = WaitForProgress(new ProgressChild(process, job, outputDrain, errorDrain),
                    subcommand, stallWindow, clock, sampled: sampled);
                if (watched.Stalled)
                {
                    completion?.CompleteIfExited(process);
                    return StallResult(subcommand, watched, outputDrain.Text, errorDrain.Text);
                }
                history.Record(subcommand, watched.LongestIdle);
                // Monitoring never waits for stdin delivery. Observe a completed write's fault,
                // but an inherited input handle cannot hold this thread after the child's exit.
                if (inputWrite?.IsFaulted == true)
                    return new GitResult(1, outputDrain.Text, inputWrite.Exception.GetBaseException().Message);
            }
            completion?.Complete(process);

            // git has exited; bound the drain so a detached grandchild holding the pipe can't keep us
            // here. With the hardening config above this should complete immediately.
            var drainDeadline = Environment.TickCount64 + DrainTimeoutMilliseconds;
            var outputDrained = outputDrain.Join(drainDeadline);
            var errorDrained = errorDrain.Join(drainDeadline);
            if (!outputDrained || !errorDrained)
            {
                TryKillTree(process);
                var drainDiagnostic = PipeDrain.DescribeTimeout(
                    "git",
                    DrainTimeoutMilliseconds,
                    outputDrain,
                    errorDrain);
                var timedOutError = errorDrain.Text;
                return new GitResult(
                    process.ExitCode,
                    outputDrain.Text,
                    string.IsNullOrEmpty(timedOutError) ? drainDiagnostic : timedOutError + Environment.NewLine + drainDiagnostic,
                    DrainTimedOut: true);
            }

            return new GitResult(process.ExitCode, outputDrain.Text, errorDrain.Text);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
        {
            return new GitResult(1, string.Empty, ex.Message, ProcessStarted: processStarted);
        }
    }

    // Instance-scoped history and observations let real-process tests exercise the default branch
    // without installing ambient observers or depending on another test's completion history.
    internal static GitResult RunProgressExecutable(string executable, string workingDirectory,
        string[] args, GitIdleGapHistory history, Action<GitProgressWatch.Observation>? sampled = null,
        Action<HandleListLaunchedProcess>? started = null,
        Action<ProcessStartInfo>? configureStart = null) =>
        RunExecutableCore(executable, workingDirectory, null, null, args, null, TimeProvider.System,
            startedObserver: started, idleGapHistory: history, sampled: sampled, configureStart: configureStart);

    internal static GitProgressWatch.Result WaitForProgress(GitProgressWatch.IChild child,
        string subcommand, TimeSpan? stallWindow, TimeProvider clock,
        Action<string>? progressOutput = null, Action<GitProgressWatch.Observation>? sampled = null) =>
        GitProgressWatch.Wait(child, stallWindow, clock, observation =>
        {
            var text = $"git {subcommand} waiting: {observation.Describe()}; stallWindowMs=" +
                (stallWindow?.TotalMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none");
            try { (progressOutput ?? Console.Error.WriteLine)(text); }
            catch (Exception) { /* Progress output must not change the command result. */ }
        }, sampled);

    internal static GitResult StallResult(string subcommand, GitProgressWatch.Result watched,
        string output, string error) => new(-1, output, PipeDrain.AppendDiagnostic(error,
            FormattableString.Invariant($"git {subcommand} stalled: {watched.Last.Describe()}; stallWindowMs={watched.StallWindow?.TotalMilliseconds}")));

    internal static string SubcommandKey(IReadOnlyList<string> args)
    {
        var index = 0;
        while (index < args.Count)
        {
            var argument = args[index];
            if (argument == "--") { index++; break; }
            if (!argument.StartsWith('-')) break;
            index++;
            if (argument is "-c" or "-C" or "--git-dir" or "--work-tree" or "--namespace" or "--config-env" or "--super-prefix")
                index++;
        }
        if (index >= args.Count) return string.Empty;
        var command = args[index++];
        // These commands have an operation in addition to their operand values.
        var operations = command switch
        {
            "worktree" => "add list lock move prune remove repair unlock",
            "remote" => "add rename remove set-head set-branches get-url set-url show prune update",
            "submodule" => "add status init deinit update set-branch set-url summary foreach sync absorbgitdirs",
            "stash" => "list show drop pop apply branch push save clear create store",
            "bisect" => "start bad good new old terms skip reset visualize replay log run",
            "notes" => "list add copy append edit show merge remove prune get-ref",
            "reflog" => "show list expire delete exists",
            "sparse-checkout" => "list init set add reapply disable check-rules",
            "maintenance" => "run start stop register unregister",
            "multi-pack-index" => "write verify expire repack",
            "commit-graph" => "write verify",
            _ => string.Empty
        };
        if (operations.Length > 0)
        {
            while (index < args.Count && args[index].StartsWith('-'))
            {
                var option = args[index++];
                if (command == "notes" && option == "--ref") index++;
            }
            if (index < args.Count && operations.Split(' ').Contains(args[index], StringComparer.Ordinal))
                return command + " " + args[index];
        }
        return command;
    }

    private static SafeFileHandle? CreateAccountingJob()
    {
        if (!OperatingSystem.IsWindows()) return null;
        SafeFileHandle? job = null;
        try
        {
            job = OwnedProcessGroup.WindowsJob.CreateKillOnCloseJob(allowBreakaway: false);
            // This job observes CPU only. Successful git helpers retain their existing lifetime.
            if (OwnedProcessGroup.WindowsJob.TryDisableKillOnClose(job)) return job;
        }
        catch (System.ComponentModel.Win32Exception) { /* Missing telemetry is unknown, never idle. */ }
        job?.Dispose();
        return null;
    }

    private sealed class ProgressChild(HandleListLaunchedProcess process, SafeFileHandle? job,
        PipeDrain stdout, PipeDrain stderr) : GitProgressWatch.IChild
    {
        public bool HasExited => process.HasExited;
        public bool WaitForExit(TimeSpan interval) => process.WaitForExit(interval);
        public long StdoutBytes => stdout.ReceivedByteCount;
        public long StderrBytes => stderr.ReceivedByteCount;
        public TimeSpan? ProcessorTime => job is not null && OwnedProcessGroup.TryReadAccounting(job, out var accounting)
            ? TimeSpan.FromMilliseconds(accounting.CpuMilliseconds) : null;
        public void KillTree()
        {
            if (job is not null && OwnedProcessGroup.WindowsJob.TryTerminate(job)) return;
            TryKillTree(process);
        }
    }

    internal static void NotifyLaunch(Action<string> observer, string[] args)
    {
        try { observer(args.Length > 0 ? args[0] : string.Empty); }
        catch (Exception) { /* Launch telemetry must not change the git result. */ }
    }

    private static void TryKillTree(HandleListLaunchedProcess process)
    {
        try { process.Kill(entireProcessTree: true); }
        catch { /* best-effort: process may have already exited */ }
    }

    // Returns true when the worktree has commit-worthy uncommitted changes, or when the
    // status check fails (treating errors as dirty to prevent accidental acceptance of
    // unverified state).
    public static bool IsWorktreeDirty(string workingDirectory)
    {
        var inspection = InspectWorktreeStatus(workingDirectory);
        return !inspection.Succeeded || inspection.IsDirty;
    }

    internal static WorktreeStatusInspection InspectWorktreeStatus(string workingDirectory) =>
        InspectWorktreeStatus(workingDirectory, DefaultTimeoutMilliseconds);

    internal static WorktreeStatusInspection InspectWorktreeStatus(string workingDirectory, int timeoutMilliseconds)
    {
        var result = Run(workingDirectory, timeoutMilliseconds, "status", "--porcelain=v1", "--untracked-files=all");
        return result.ExitCode == 0
            ? new WorktreeStatusInspection(true, ParseCommitWorthyStatusPaths(result.Output), null)
            : new WorktreeStatusInspection(false, [], string.IsNullOrWhiteSpace(result.Error) ? "git status failed" : result.Error.Trim());
    }

    internal static AheadBehindInspection InspectAheadBehind(string workingDirectory, string integrationBranch)
    {
        var result = Run(workingDirectory, "rev-list", "--left-right", "--count", $"{integrationBranch}...HEAD");
        return InspectAheadBehind(result);
    }

    internal static AheadBehindInspection InspectAheadBehind(GitResult result)
    {
        if (!result.Succeeded || result.DrainTimedOut)
        {
            return new AheadBehindInspection(
                false,
                null,
                null,
                string.IsNullOrWhiteSpace(result.Error) ? "git rev-list failed" : result.Error.Trim());
        }

        var parts = result.Output.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 ||
            !int.TryParse(parts[0], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var behind) ||
            !int.TryParse(parts[1], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var ahead))
        {
            return new AheadBehindInspection(false, null, null, "git rev-list returned malformed counts");
        }

        return new AheadBehindInspection(true, ahead, behind, null);
    }

    internal static string FilterCommitWorthyStatus(string statusOutput)
    {
        if (string.IsNullOrWhiteSpace(statusOutput))
        {
            return statusOutput;
        }

        var lines = statusOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Where(IsCommitWorthyStatusLine);
        return string.Join("\n", lines);
    }

    internal static string[] ParseCommitWorthyStatusPaths(string statusOutput)
    {
        if (string.IsNullOrWhiteSpace(statusOutput))
        {
            return [];
        }

        return statusOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .SelectMany(ParseStatusLinePaths)
            .Where(path => !IsOrchestratorInternalArtifactPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    internal static bool IsCommitWorthyStatusLine(string line)
    {
        var paths = ParseStatusLinePaths(line);
        return paths.Length > 0 && paths.Any(path => !IsOrchestratorInternalArtifactPath(path));
    }

    internal static string[] ParseStatusLinePaths(string line)
    {
        if (line.Length < 4)
        {
            return [];
        }

        var path = line[3..].Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return [];
        }

        var renameSeparator = path.IndexOf(" -> ", StringComparison.Ordinal);
        if (renameSeparator >= 0)
        {
            var source = path[..renameSeparator].Trim();
            var destination = path[(renameSeparator + 4)..].Trim();
            return [source, destination];
        }

        return [path];
    }

    internal static bool IsOrchestratorInternalArtifactPath(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return normalized.Equals(".qwen/settings.json", StringComparison.OrdinalIgnoreCase) ||
            IsPowerShellModuleAnalysisCachePath(normalized) ||
            normalized.Equals(".orchestrator-handoff.md", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(WorkerSandboxPreparer.MarkerFileName, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals(WorkerSandboxPreparer.ReceiptFileName, StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("WORKER_RESULT.md", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("WORKER_RESULT.txt", StringComparison.OrdinalIgnoreCase) ||
            IsIsolationLeaseArtifactPath(normalized) ||
            normalized.StartsWith(".orchestrator-context/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/obj/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".scratch/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith(".orchestrator-prototype/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("TestResults/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/TestResults/", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("playwright-report/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/playwright-report/", StringComparison.OrdinalIgnoreCase) ||
            normalized.EndsWith(".log", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPowerShellModuleAnalysisCachePath(string normalizedPath)
    {
        if (normalizedPath.Equals(
            "Microsoft/Windows/PowerShell/ModuleAnalysisCache",
            StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        const string powerShell7Prefix = "Microsoft/PowerShell/ModuleAnalysisCache-";
        if (!normalizedPath.StartsWith(powerShell7Prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var cacheKey = normalizedPath[powerShell7Prefix.Length..];
        return cacheKey.Length == 8 && cacheKey.All(Uri.IsHexDigit);
    }

    private static bool IsIsolationLeaseArtifactPath(string normalizedPath)
    {
        return
            (normalizedPath.StartsWith("i/goals/", StringComparison.OrdinalIgnoreCase) &&
             (normalizedPath.EndsWith("/lease/lease.json", StringComparison.OrdinalIgnoreCase) ||
              normalizedPath.EndsWith("/lease/lease.lock", StringComparison.OrdinalIgnoreCase))) ||
            (normalizedPath.StartsWith("i/slots/", StringComparison.OrdinalIgnoreCase) &&
             normalizedPath.EndsWith("/lease.execution.lock", StringComparison.OrdinalIgnoreCase));
    }
}
