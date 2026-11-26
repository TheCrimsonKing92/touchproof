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
        if (RunFromConsole(args) != 0)
            throw new InvalidOperationException("Shadow startup check failed.");
        return false;
    }

    internal static int RunFromConsole(IReadOnlyList<string> args)
    {
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler handler = (_, e) =>
        {
            e.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += handler;
        try { return Run(args, Console.Out, Console.Error, cancellationToken: cancellation.Token); }
        finally { Console.CancelKeyPress -= handler; }
    }

    internal static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr,
        ShadowStartHooks? hooks = null, CancellationToken cancellationToken = default) =>
        RunAsync(args, stdout, stderr, hooks ?? new(), cancellationToken).GetAwaiter().GetResult();

    private static async Task<int> RunAsync(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr,
        ShadowStartHooks hooks, CancellationToken cancellationToken)
    {
        var assertion = "arguments";
        string? scratch = null;
        Options? options = null;
        try
        {
            options = Parse(args);
            assertion = "scratch-containment";
            var source = ResolveDirectoryPath(options.Source);
            scratch = ResolveDirectoryPath(options.Scratch);
            if (!Directory.Exists(source))
                throw new IOException($"Source state directory does not exist: {source}");
            if (ContainsPath(source, scratch))
                throw new IOException("Scratch must be outside the source state directory.");

            var registryDirectory = ResolveDirectoryPath(hooks.RegistryDirectory ??
                Path.Combine(OrchestratorDataRoot.Resolve().RootDirectory, "shadow-start", "runs"));
            var stagingParent = ResolveDirectoryPath(hooks.StagingParent ??
                Path.Combine(OrchestratorTempRoot.GetRoot(), "tmp", "store-inspection"));
            var id = Guid.NewGuid();
            var state = Path.Combine(scratch, StateDirectoryName);
            var staging = Path.Combine(stagingParent, id.ToString("N"));
            if (ShadowStartRunRegistry.Overlaps(registryDirectory, source) ||
                ShadowStartRunRegistry.Overlaps(registryDirectory, scratch) ||
                ShadowStartRunRegistry.Overlaps(staging, source) ||
                ShadowStartRunRegistry.Overlaps(staging, scratch) ||
                ShadowStartRunRegistry.Overlaps(staging, registryDirectory))
                throw new IOException("Shadow-start registry and staging must be outside source and scratch.");

            assertion = "ownership-registry";
            using var registry = OpenRegistry(registryDirectory);
            assertion = "scratch-containment";
            registry.Recover(source, stderr);
            if (EntryExists(options.Scratch) || EntryExists(scratch))
                throw new IOException("Scratch directory must be new.");

            assertion = "capacity-preflight";
            var plan = ShadowStartCopyPlan.Create(source, state, staging, registryDirectory, options.Retention, hooks);
            var shortfalls = plan.Capacity.Where(volume => volume.ShortfallBytes > 0).ToArray();
            if (shortfalls.Length > 0)
            {
                Report(stderr, new(assertion, "Insufficient free space for shadow-start."), scratch, options.Retention);
                foreach (var volume in shortfalls)
                    stderr.WriteLine(FormattableString.Invariant(
                        $"volume={volume.Volume} required_bytes={volume.RequiredBytes} available_bytes={volume.AvailableBytes} shortfall_bytes={volume.ShortfallBytes}"));
                return 1;
            }

            cancellationToken.ThrowIfCancellationRequested();
            ShadowStartRun? run = null;
            if (plan.Retention == ShadowScratchRetention.Disposable)
            {
                assertion = "ownership-registry";
                using var owner = Process.GetCurrentProcess();
                run = new(id, plan.Retention, source, scratch, state, staging,
                    plan.Stores.Select(store => Path.GetFileName(store.Destination)).ToArray(),
                    owner.Id, owner.StartTime.ToUniversalTime(),
                    ShadowStartPhase.Copying);
                registry.Save(run);
            }

            ShadowStartMeasurement measurement;
            try
            {
                assertion = "database-backup";
                CreateScratch(scratch);
                Directory.CreateDirectory(state);
                if (run is not null && OperatingSystem.IsWindows())
                    UpdateRun(run with
                    {
                        ScratchIdentity = ShadowStartReclaimer.CaptureDirectory(scratch),
                        StateIdentity = ShadowStartReclaimer.CaptureDirectory(state)
                    });
                if (run is not null && plan.Stores.Count > 0)
                {
                    CreateScratch(staging);
                    if (OperatingSystem.IsWindows())
                        UpdateRun(run with { StagingIdentity = ShadowStartReclaimer.CaptureDirectory(staging) });
                }
                var trackCopies = run is not null && OperatingSystem.IsWindows();
                CopyStateDatabases(plan, hooks, trackCopies ? CreateOwnedStore : null,
                    trackCopies ? PrepareStagedDatabase : null, cancellationToken);
                assertion = "candidate-launch";
                measurement = await SuperviseAsync(options, scratch, state, run,
                    run is null ? null : registry.SettlementPath(run.Id), SetPhase, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (IsRunFailure(ex))
            {
                measurement = new(ex is OperationCanceledException ? "cancelled" : assertion, ex.Message,
                    ReleaseVerified: run?.Phase == ShadowStartPhase.Copying);
            }

            ShadowStartCleanup? cleanup = null;
            if (run is not null)
                cleanup = registry.Cleanup(run, measurement.ReleaseVerified ? null :
                    OperatingSystem.IsWindows() ? "process-group-unconfirmed" : "release-unverifiable-platform",
                    measurement, plan.Capacity, options.Candidate ?? typeof(CliShadowStartCommand).Assembly.Location);
            Report(measurement.Assertion is null ? stdout : stderr, measurement, scratch, plan.Retention, id, cleanup);
            if (measurement.Assertion is not null && measurement.OutputTail is { } tail)
                foreach (var line in tail) stderr.WriteLine(line);
            return measurement.Assertion is not null ? 1 : cleanup?.Status == "deferred" ? 2 : 0;

            void SetPhase(ShadowStartPhase phase, SpawnProcessIdentity? candidate)
            {
                if (run is null) return;
                if (phase == ShadowStartPhase.Running)
                {
                    // Establish empty destination sidecar slots before the suspended candidate can run.
                    foreach (var name in run.Databases)
                        RequireNoDestinationSidecars(Path.Combine(run.State, name));
                }
                UpdateRun(run with
                {
                    Phase = phase,
                    CandidatePid = candidate?.ProcessId,
                    CandidateStarted = candidate?.StartedAt,
                    CandidateImagePath = candidate?.ImagePath,
                    CandidateMayCreateSidecars = phase == ShadowStartPhase.Running
                });
            }

            void UpdateRun(ShadowStartRun updated)
            {
                registry.Save(updated);
                run = updated;
            }

            FileStream CreateOwnedStore(string path) =>
                ShadowStartReclaimer.CreateRegisteredStore(path, identity =>
                {
                    var current = run!;
                    var isDatabase = current.Databases.Contains(Path.GetFileName(path), StringComparer.Ordinal);
                    var recorded = isDatabase ? current.StoreIdentities : current.CreatedSidecarIdentities;
                    var identities = recorded is null ? new Dictionary<string, ShadowFileIdentity>() :
                        new Dictionary<string, ShadowFileIdentity>(recorded);
                    identities.Add(path, identity);
                    UpdateRun(isDatabase ? current with { StoreIdentities = identities } :
                        current with { CreatedSidecarIdentities = identities });
                });

            void PrepareStagedDatabase(string database)
            {
                // A read-only SQLite open can still initialize WAL and shared memory in staging.
                foreach (var path in new[] { database + "-wal", database + "-shm" })
                    if (run!.CreatedSidecarIdentities?.ContainsKey(path) != true)
                        CreateOwnedStore(path).Dispose();
            }

            ShadowStartRunRegistry OpenRegistry(string path)
            {
                try { return new(path); }
                catch (IOException ex) when ((ex.HResult & 0xffff) is 32 or 33 ||
                    (!OperatingSystem.IsWindows() && (ex.HResult & 0xffff) == 11))
                {
                    assertion = "concurrent-run";
                    throw;
                }
            }
        }
        catch (Exception ex) when (IsRunFailure(ex))
        {
            Report(stderr, new(ex is OperationCanceledException ? "cancelled" : assertion, ex.Message),
                scratch, options?.Retention);
            return 1;
        }
    }

    private static bool IsRunFailure(Exception ex) => ex is ArgumentException or IOException or UnauthorizedAccessException or
        InvalidOperationException or SqliteException or Win32Exception or NotSupportedException or
        OverflowException or OperationCanceledException;

    internal static bool ContainsPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path);
        return !Path.IsPathRooted(relative) && relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
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
                "--startup-ceiling-seconds" or "--budget-seconds" or "--scratch-retention"))
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
        var retention = values.GetValueOrDefault("--scratch-retention")?.ToLowerInvariant() switch
        {
            null or "retain" => ShadowScratchRetention.Retain,
            "disposable" => ShadowScratchRetention.Disposable,
            _ => throw new ArgumentException("--scratch-retention requires retain or disposable.")
        };
        return new(Absolute("--source"), Absolute("--scratch"), candidate, candidateArguments, startup, budget, retention);

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

    private static void CopyStateDatabases(ShadowStartCopyPlan plan, ShadowStartHooks hooks,
        Func<string, FileStream>? createOwnedStore, Action<string>? prepareStagedDatabase,
        CancellationToken cancellationToken)
    {
        foreach (var store in plan.Stores)
        {
            cancellationToken.ThrowIfCancellationRequested();
            hooks.BeforeCopy?.Invoke(store);
            // A direct read-only open of a quiet WAL database can create source sidecars.
            using var snapshot = new StoreInspectionSnapshot(store.Source,
                stagingDirectory: plan.Retention == ShadowScratchRetention.Disposable ? Path.GetDirectoryName(store.Staged) : null,
                createStagedFile: createOwnedStore, prepareStagedDatabase: prepareStagedDatabase);
            createOwnedStore?.Invoke(store.Destination).Dispose();
            hooks.BeforeBackup?.Invoke(store);
            if (createOwnedStore is not null)
                RequireNoDestinationSidecars(store.Destination);
            using (var copy = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = store.Destination, Mode = SqliteOpenMode.ReadWriteCreate, Pooling = false
            }.ToString()))
            {
                copy.Open();
                if (createOwnedStore is not null)
                {
                    // This fresh destination will be discarded after a failed backup.
                    // Keep its rollback journal in memory so an interrupted backup leaves only registered files.
                    using var journal = copy.CreateCommand();
                    journal.CommandText = "PRAGMA journal_mode=MEMORY";
                    if (!string.Equals(journal.ExecuteScalar() as string, "memory", StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Cannot configure the private backup journal: {store.Destination}.");
                }
                snapshot.Connection.BackupDatabase(copy);
            }
        }
    }

    private static void RequireNoDestinationSidecars(string database)
    {
        foreach (var suffix in ShadowStartCopyPlan.Suffixes.Skip(1))
        {
            var sidecar = database + suffix;
            if (EntryExists(sidecar))
                throw new IOException($"Destination sidecar already exists: {sidecar}.");
        }
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

    private static async Task<ShadowStartMeasurement> SuperviseAsync(Options options, string scratch, string state,
        ShadowStartRun? run, string? settlementPath, Action<ShadowStartPhase, SpawnProcessIdentity?> setPhase,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = BuildLaunch(options, scratch, state);
        var logs = Path.Combine(state, "logs");
        var stopFile = Path.Combine(scratch, ConductorBatchLoop.StopFileName);
        var tail = new Queue<string>();
        var evidence = new JournalEvidence();
        DateTimeOffset? stopRequested = null;
        long? peak = null;
        string? failure = null;
        string? detail = null;

        setPhase(ShadowStartPhase.LaunchPending, null);
        using var suspended = run is not null && OperatingSystem.IsWindows()
            ? OwnedProcessGroup.StartSuspendedContainedRedirected(info) : null;
        using var ordinaryGroup = suspended is null ? OwnedProcessGroup.CreateContained() : null;
        var group = suspended?.Group ?? ordinaryGroup!;
        var launchedAt = DateTimeOffset.UtcNow;
        var elapsed = Stopwatch.StartNew();
        HandleListLaunchedProcess? child = null;
        DurableAcceptanceJob? custody = null;
        try
        {
            if (suspended is null)
                child = group.StartRedirected(info);
            else
            {
                var candidate = suspended.ReadSuspendedIdentity();
                setPhase(ShadowStartPhase.LaunchPending, candidate);
                // The outer job still kills on supervisor death. The independent custodian holds
                // only the nested job, so it can prove release after that death.
                custody = DurableAcceptanceJob.Attach(suspended, run!.JobName,
                    AcceptanceJobCustodianHost.BindShadowStart(scratch, state), settlementPath);
                setPhase(ShadowStartPhase.Running, candidate);
                cancellationToken.ThrowIfCancellationRequested();
                launchedAt = DateTimeOffset.UtcNow;
                elapsed.Restart();
                suspended.Resume();
            }
        }
        catch (Exception ex) when (IsRunFailure(ex))
        {
            var released = OperatingSystem.IsWindows() && group.TerminateAndConfirmEmpty(CleanupBudget);
            var custodyFailure = await WaitForCustodianAsync(released).ConfigureAwait(false);
            return new(ex is OperationCanceledException ? "cancelled" : "candidate-launch",
                custodyFailure is null ? ex.Message : $"{ex.Message} {custodyFailure}", ReleaseVerified: released);
        }
        using var launchedChild = child;
        using var attached = child is null ? null : TryAttach(child);
        var process = suspended?.Process ?? attached;
        var input = suspended?.StandardInput ?? child!.StandardInput;
        var output = DrainAsync(suspended?.StandardOutput ?? child!.StandardOutput, "stdout");
        var error = DrainAsync(suspended?.StandardError ?? child!.StandardError, "stderr");
        bool confirmed;
        try
        {
            input.Close();
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (TryReadPeak(process) is { } sample)
                    peak = Math.Max(peak ?? 0, sample);
                var exited = child?.HasExited ?? process!.HasExited;
                if (exited)
                    elapsed.Stop();
                evidence.Observe(logs, launchedAt, stopRequested);
                if (elapsed.Elapsed >= options.Budget)
                    failure = "wall-clock-budget";
                else if (evidence.FirstTick is { } firstTick && firstTick - launchedAt >= options.StartupCeiling)
                    failure = "startup-ceiling";
                else if (exited)
                {
                    var exitCode = child?.ExitCode ?? process!.ExitCode;
                    (failure, detail) = !evidence.Started ? ("loop-start", "startup failure: child exited before loop-start")
                        : evidence.FirstTick is null ? ("startup-first-tick", "startup failure: child exited before first tick")
                        : stopRequested is null ? ("tick-advance", "child exited before the supervisor requested stop")
                        : exitCode != 0 ? ("exit-code", $"child exited with code {exitCode}")
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
                    using var stop = new FileStream(stopFile, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read);
                    // A candidate using a coarser clock can journal the stop at the file's creation time.
                    stopRequested = File.GetCreationTimeUtc(stop.SafeFileHandle);
                }

                var deadline = evidence.FirstTick is null ? options.StartupCeiling : options.Budget;
                var remaining = deadline - elapsed.Elapsed;
                if (remaining > TimeSpan.Zero)
                    await Task.Delay(remaining < PollInterval ? remaining : PollInterval, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (IsRunFailure(ex))
        {
            failure = ex is OperationCanceledException ? "cancelled" : "candidate-supervision";
            detail = ex.Message;
        }
        finally
        {
            confirmed = OperatingSystem.IsWindows()
                ? group.TerminateAndConfirmEmpty(CleanupBudget)
                : StopPortableGroup(group, child!);
            if (elapsed.IsRunning)
                elapsed.Stop();
            if (!confirmed)
            {
                failure ??= "candidate-cleanup";
                detail = $"{detail} Child process-tree termination could not be confirmed.".Trim();
            }
        }

        var drained = false;
        try
        {
            await Task.WhenAll(output, error).WaitAsync(CleanupBudget).ConfigureAwait(false);
            drained = true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException)
        {
            failure ??= "candidate-output";
            detail = $"{detail} Child output drain failed: {ex.Message}".Trim();
        }
        if (await WaitForCustodianAsync(confirmed).ConfigureAwait(false) is { } cleanupDetail)
        {
            failure ??= "candidate-cleanup";
            detail = $"{detail} {cleanupDetail}".Trim();
        }

        lock (tail)
            return new(failure, detail, elapsed.ElapsedMilliseconds, peak,
                evidence.FirstTick is { } observed ? (long)(observed - launchedAt).TotalMilliseconds : null,
                tail.ToArray(), OperatingSystem.IsWindows() && confirmed && drained);

        async Task<string?> WaitForCustodianAsync(bool groupReleased)
        {
            if (!groupReleased || custody is null) return null;
            try
            {
                await custody.CustodianCompletion.WaitAsync(CleanupBudget).ConfigureAwait(false);
                return null;
            }
            catch (TimeoutException)
            {
                return "Shadow-start job custodian did not exit after candidate release.";
            }
        }

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

    private static void Report(TextWriter writer, ShadowStartMeasurement measurement, string? scratch,
        ShadowScratchRetention? retention, Guid? id = null, ShadowStartCleanup? cleanup = null)
    {
        static string Measurement(long? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "unavailable";
        var line = $"SHADOW_START result={(measurement.Assertion is null ? "pass" : "fail")} assertion={measurement.Assertion ?? "none"} " +
            $"wall_ms={Measurement(measurement.WallMs)} peak_working_set_bytes={Measurement(measurement.PeakWorkingSetBytes)} " +
            $"first_tick_ms={Measurement(measurement.FirstTickMs)} scratch={scratch ?? "unavailable"}";
        if (retention is not null)
            line += $" mode={(retention == ShadowScratchRetention.Disposable ? "disposable" : "retain")}";
        if (id is not null)
            line += $" run_id={id:N}";
        if (cleanup is not null)
            line += $" cleanup={cleanup.Status} cleanup_reason={cleanup.Reason ?? "none"} cleanup_ms={Measurement(cleanup.Milliseconds)} receipt={cleanup.Receipt}";
        else if (id is not null)
            line += " cleanup=retained";
        writer.WriteLine(line);
        if (measurement.Assertion is not null)
            writer.WriteLine(measurement.Detail ?? $"Shadow startup assertion failed: {measurement.Assertion}.");
    }

    private sealed record Options(string Source, string Scratch, string? Candidate,
        IReadOnlyList<string> CandidateArguments, TimeSpan StartupCeiling, TimeSpan Budget, ShadowScratchRetention Retention);

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
