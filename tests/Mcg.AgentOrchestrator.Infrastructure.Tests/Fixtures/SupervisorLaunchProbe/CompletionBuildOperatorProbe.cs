using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using static CompletionBuildOperatorRepository;

internal static class CompletionBuildOperatorProbe
{
    private static readonly TimeSpan Guard = TimeSpan.FromMinutes(5);

    internal static int Gate(string root)
    {
        var f = Load(root);
        using var ready = EventWaitHandle.OpenExisting(f.GateToken + "-ready");
        using var release = EventWaitHandle.OpenExisting(f.GateToken + "-release");
        f.Record("compiler-checkpoint", new { image = Environment.ProcessPath,
            startedAt = Process.GetCurrentProcess().StartTime.ToUniversalTime() });
        ready.Set();
        Require(release.WaitOne(Guard), "Independent operator did not release the held compiler checkpoint.");
        f.Record("compiler-released", new { });
        return 0;
    }

    internal static async Task<int> Run(string root, string source, string control)
    {
        Require(new[] { "held", "stop", "renewal", "restart", "ownership-loss", "replacement", "stale-candidate", "stale-dispatch" }
            .Contains(control), "Unknown operator control.");
        source = Path.GetFullPath(source);
        var token = "Local\\mcg-completion-" + Guid.NewGuid().ToString("N");
        using var ready = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-ready");
        using var release = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-release");
        using var renewed = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-renewed");
        using var renewalVerifierReady = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-renewal-verifier-ready");
        using var successorReady = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-successor-ready");
        using var successorRelease = new EventWaitHandle(false, EventResetMode.ManualReset, token + "-successor-release");
        var f = Create(root, source, token);
        Process? successor = null;
        var hostExitUnconfirmed = false;
        try
        {
            f.Record("candidate", new { control, sourceHead = Git(source, "rev-parse", "HEAD"),
                sourceDirty = Git(source, "status", "--porcelain"), f.State, f.Storage,
                application = typeof(ConductorBatchLoop).Assembly.Location,
                applicationSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(ConductorBatchLoop).Assembly.Location))),
                fixtureSha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(typeof(CompletionBuildOperatorProbe).Assembly.Location))) });
            await Host("start");
            Require(ready.WaitOne(Guard), "Real managed build did not reach the compiler checkpoint.");
            var original = Operation(f);
            File.WriteAllText(Path.Combine(root, "original-operation.json"), JsonSerializer.Serialize(original));
            Pending(f, original);
            Require(original.Projects.SequenceEqual(["src/Feature/Feature.csproj"]), "Unexpected required project identity.");
            var disjointGoal = new GoalId(f.DisjointGoal);
            var disjointTask = new TaskId(f.DisjointTask);
            var disjointBefore = f.LoadKernel().GetTask(disjointGoal, disjointTask).Status;
            Require(disjointBefore == WorkTaskStatus.Running, "Disjoint task was not running before the progress tick.");
            f.Record("held-disjoint-before-progress", new { original.Id, disjointGoal = f.DisjointGoal,
                disjointTask = f.DisjointTask, status = disjointBefore.ToString() });
            await Host("progress");
            Require(f.LoadKernel().GetGoal(disjointGoal).Priority == GoalPriority.Critical, "Operator intent did not mutate goal priority.");
            var intent = await new SqliteOperatorIntentStore(Path.Combine(root, "operator-intents.db"), root)
                .GetAsync(File.ReadAllText(Path.Combine(root, "intent-id.txt")));
            Require(intent?.Status == OperatorIntentStatus.Applied, "Real operator intent was not durably applied.");
            Pending(f, original);
            // The conductor defers walking goals changed by an intent until the next tick.
            await Host("observe");
            var disjointAfter = f.LoadKernel().GetTask(disjointGoal, disjointTask).Status;
            Require(disjointAfter == WorkTaskStatus.Completed, "Disjoint tracked goal did not progress while build was held.");
            Pending(f, original);
            Require(!release.WaitOne(0), "Held build was released before unrelated progress and intent application.");
            f.Record("held-control-proven", new { original.Id, original.DispatchId, original.TreeDigest,
                original.ChildPid, original.ChildStartedAt, original.ChildImagePath, intentId = intent!.Id, intent.CompletedAt,
                releaseSignalled = false, disjointGoal = f.DisjointGoal, disjointTask = f.DisjointTask,
                disjointStatus = disjointAfter.ToString() });

            switch (control)
            {
                case "stop":
                    await Host("stop");
                    Pending(f, original); // Graceful conductor stop preserves the authorized child.
                    File.Delete(f.Stop);
                    await Host("cancel"); // Explicit operator cancellation is a separate contract.
                    Require(Operation(f) is { Phase: CompletionBuildEvidencePhase.Cancelled, CustodyReleased: true }, "Explicit cancel did not settle custody.");
                    Require(CompletionBuildEvidenceChildIdentity.Probe(original) == CompletionBuildChildLiveness.Dead, "Cancelled child is still alive.");
                    NoCommit(f);
                    break;
                case "renewal":
                    await Host("renewal");
                    Require(renewed.WaitOne(0), "Real handoff successor did not tick.");
                    Pending(f, original);
                    goto case "held";
                case "restart":
                    // Each Host call is a separate OS process loading SQLite. Original launch
                    // owner has exited; a restarted host must adopt, never launch another child.
                    Require(!new ConductLockPidProbe().IsSameProcess(original.LaunchOwnerPid, original.LaunchOwnerStartedAt), "Original launch owner did not exit.");
                    await Host("observe");
                    Pending(f, original);
                    goto case "held";
                case "ownership-loss":
                    successor = StartHost(f, "lease-successor");
                    Require(successorReady.WaitOne(Guard), "Real successor lease owner did not become ready.");
                    var successorOwner = JsonDocument.Parse(File.ReadAllText(Path.Combine(root, "successor.json")))
                        .RootElement.GetProperty("owner").GetString();
                    await Host("cancel-after-ownership-loss");
                    var cancelled = Operation(f);
                    Require(cancelled is { Phase: CompletionBuildEvidencePhase.Cancelled, CustodyReleased: true } &&
                        CompletionBuildEvidenceCoordinator.MatchesResult(original, cancelled),
                        "Ownership loss did not persist cancellation and custody release for the original child.");
                    Require(!successor.HasExited, "Old operation terminated the valid successor.");
                    Require(new ReconcileSweepRemediationStore(f.State).TryGetAcceptanceLeaseOwner(f.Goal) == successorOwner,
                        "Old operation released the valid successor's lease.");
                    Require(CompletionBuildEvidenceChildIdentity.Probe(original) == CompletionBuildChildLiveness.Dead, "Old child custody not settled.");
                    NoCommit(f);
                    f.Record("successor-preserved", new { successorOwner, successorPid = successor.Id, cancelled.Id,
                        cancelled.DispatchId, cancelled.ChildPid, phase = cancelled.Phase.ToString(), cancelled.CustodyReleased });
                    successorRelease.Set();
                    await WaitForHost(successor, "lease-successor");
                    Require(successor.ExitCode == 0, "Successor failed to settle its own lease.");
                    break;
                case "replacement":
                    await Host("reset");
                    await Host("cancel-attached");
                    ready.Reset();
                    await Host("replace");
                    Require(ready.WaitOne(Guard), "Replacement child did not reach the real checkpoint.");
                    var replacement = Operation(f);
                    Require(replacement.Id != original.Id && replacement.DispatchId != original.DispatchId,
                        "Replacement reused obsolete attempt authority.");
                    await Host("obsolete-cancel");
                    Pending(f, replacement);
                    await Host("cancel-attached");
                    NoCommit(f);
                    break;
                case "stale-candidate":
                    await ReleaseAndWait(f, original, release);
                    await Host("stale-candidate");
                    Require(Operation(f).Phase == CompletionBuildEvidencePhase.StaleCandidate,
                        "Old compiler success was not rejected after source mutation.");
                    NoCommit(f);
                    break;
                case "stale-dispatch":
                    await ReleaseAndWait(f, original, release);
                    File.Copy(original.ResultPath, Path.Combine(root, "obsolete-result.json"));
                    await Host("reset");
                    await Host("settle");
                    Require(Operation(f) is { Phase: CompletionBuildEvidencePhase.Success, Applied: false, CustodyReleased: true },
                        "Historical compiler fact was applied to the reset worker.");
                    NoCommit(f);
                    File.Move(f.WorkerBuildReceiptPath(f.GoalId), Path.Combine(root, "obsolete-worker-build-receipt.json"));
                    ready.Reset();
                    release.Reset();
                    await Host("replace");
                    Require(ready.WaitOne(Guard), "New dispatch did not start its own real build.");
                    var next = Operation(f);
                    Require(next.Id != original.Id && next.DispatchId != original.DispatchId,
                        "Stale-dispatch control did not attach a distinct dispatch and build attempt.");
                    Require(next.TreeDigest == original.TreeDigest && next.Projects.SequenceEqual(original.Projects),
                        "Stale-dispatch control changed the candidate or required projects.");
                    Pending(f, next);
                    await ReleaseAndWait(f, next, release);
                    // Present the actual old child's receipt to the new attempt. A current
                    // successful worker-build receipt must not rescue its obsolete authority.
                    File.Copy(Path.Combine(root, "obsolete-result.json"), next.ResultPath, overwrite: true);
                    f.Record("stale-dispatch-replay", new { obsoleteAttemptId = original.Id, obsoleteDispatchId = original.DispatchId,
                        currentAttemptId = next.Id, currentDispatchId = next.DispatchId, next.TreeDigest, next.Projects });
                    await Host("observe");
                    Require(Operation(f).Phase == CompletionBuildEvidencePhase.ApparatusFailure,
                        "Obsolete dispatch receipt authorized the new attempt.");
                    NoCommit(f);
                    break;
                case "held":
                    await ReleaseAndWait(f, original, release);
                    await Host("observe");
                    var applied = f.LoadKernel().GetTask(f.GoalId, f.TaskId);
                    Require(applied.Status == WorkTaskStatus.Completed && applied.LastVerification?.Succeeded == true &&
                        applied.CompletionBuildEvidence is { Applied: true, CustodyReleased: true, Phase: CompletionBuildEvidencePhase.Success } &&
                        applied.LastDispatch?.ResultCommit is not null, "Applicable compiler verdict did not authorize completion and commit.");
                    var head = Git(original.WorktreeRoot, "rev-parse", "HEAD");
                    await Host("observe");
                    Require(Git(original.WorktreeRoot, "rev-parse", "HEAD") == head &&
                        f.LoadKernel().GetTask(f.GoalId, f.TaskId).VerificationHistory.Count == 1, "Terminal outcome was applied more than once.");
                    break;
            }
        }
        catch (Exception ex)
        {
            f.Record("control-failed", new { control, error = ex.ToString() });
            Console.Error.WriteLine($"control={control}; failed={ex.Message}; receipts={f.Receipts}");
            return 1;
        }
        finally
        {
            successorRelease.Set();
            try
            {
                using (successor)
                {
                    if (successor is not null) await WaitForHost(successor, "lease-successor");
                }
            }
            finally
            {
                // A fixture host can still hold the conductor lease until its exit is proven.
                if (!hostExitUnconfirmed)
                {
                    File.Delete(f.Stop);
                    if (OperationOrNull(f) is { IsPending: true }) await Host("cancel-attached");
                    var terminal = OperationOrNull(f);
                    Require(terminal?.ChildPid is null || CompletionBuildEvidenceChildIdentity.Probe(terminal) == CompletionBuildChildLiveness.Dead,
                        "Scenario cleanup could not prove child death; retain the printed root for recovery.");
                    f.Record("custody-settled", new { terminal?.Id, terminal?.CustodyReleased });
                }
            }
        }
        f.Record("control-passed", new { control });
        Console.WriteLine(JsonSerializer.Serialize(new { control, result = "passed", receipts = f.Receipts }));
        return 0;

        async Task Host(string action)
        {
            using var process = StartHost(f, action);
            await WaitForHost(process, action);
            Require(process.ExitCode == 0, $"Host {action} exited {process.ExitCode}; inspect {f.Root}/host-{action}.err.log.");
        }

        async Task WaitForHost(Process process, string action)
        {
            try
            {
                await Wait(process);
            }
            catch (Exception waitFailure)
            {
                var identity = $"{action} (PID {process.Id})";
                try
                {
                    if (!process.HasExited)
                    {
                        // Use the handle retained by StartHost. Managed child and successor custody have their own owners.
                        try { process.Kill(entireProcessTree: false); }
                        catch (Exception ex) when ((ex is InvalidOperationException or System.ComponentModel.Win32Exception) && process.HasExited) { }
                        using var exitGuard = new CancellationTokenSource(Guard);
                        await process.WaitForExitAsync(exitGuard.Token);
                    }
                }
                catch (Exception settlementFailure)
                {
                    hostExitUnconfirmed = true;
                    throw new InvalidOperationException($"Could not confirm fixture host {identity} exited; retain {f.Root} for recovery.",
                        new AggregateException(waitFailure, settlementFailure));
                }
                finally
                {
                    Drains.Remove(process.Id);
                }
                if (waitFailure is OperationCanceledException)
                    throw new TimeoutException($"Timed out waiting for fixture host {identity}. Host exit confirmed.", waitFailure);
                throw;
            }
        }
    }

    private static CompletionBuildEvidenceOperation? OperationOrNull(CompletionBuildOperatorRepository f) =>
        f.LoadKernel().GetTask(f.GoalId, f.TaskId).CompletionBuildEvidence;
    private static CompletionBuildEvidenceOperation Operation(CompletionBuildOperatorRepository f) =>
        OperationOrNull(f) ?? throw new InvalidOperationException("No managed operation was attached.");
    private static void Pending(CompletionBuildOperatorRepository f, CompletionBuildEvidenceOperation expected)
    {
        var task = f.LoadKernel().GetTask(f.GoalId, f.TaskId);
        Require(task.CompletionBuildEvidence is { IsPending: true, Applied: false, CustodyReleased: false } actual &&
            actual.HasSameOrigin(expected) && CompletionBuildEvidenceTarget.From(actual) == CompletionBuildEvidenceTarget.From(expected) &&
            CompletionBuildEvidenceChildIdentity.Probe(actual) == CompletionBuildChildLiveness.Live,
            "Pending attempt identity, authority or real child changed across ticks.");
        Require(task.Status == WorkTaskStatus.Running && task.LastVerification is null, "Pending exited worker acquired a terminal verdict.");
        NoCommit(f);
    }
    private static void NoCommit(CompletionBuildOperatorRepository f)
    {
        var task = f.LoadKernel().GetTask(f.GoalId, f.TaskId);
        Require(task.Status != WorkTaskStatus.Completed && task.DispatchHistory.All(d => d.ResultCommit is null), "Unproven result authorized completion/commit.");
        Require(Git(GoalWorktrees.WorktreePath(f.Repository, f.GoalId), "rev-parse", "HEAD") == f.InitialHead, "Unproven candidate was committed.");
    }
    private static async Task ReleaseAndWait(CompletionBuildOperatorRepository f, CompletionBuildEvidenceOperation op, EventWaitHandle release)
    {
        Require(CompletionBuildEvidenceChildIdentity.Probe(op) == CompletionBuildChildLiveness.Live, "Child identity unavailable at release.");
        using var child = Process.GetProcessById(op.ChildPid!.Value);
        _ = child.Handle;
        Require(new DateTimeOffset(child.StartTime.ToUniversalTime()) == op.ChildStartedAt, "Child PID was reused.");
        f.Record("operator-release", new { op.Id, op.DispatchId, op.TreeDigest, op.ChildPid });
        release.Set();
        await Wait(child);
        var result = JsonSerializer.Deserialize<CompletionBuildEvidenceOperation>(File.ReadAllText(op.ResultPath))!;
        Require(child.ExitCode == 0 && result.Phase == CompletionBuildEvidencePhase.Success &&
            CompletionBuildEvidenceCoordinator.MatchesResult(op, result), "Real managed child did not emit applicable compiler success.");
        f.Record("real-compiler-result", new { result.Id, result.Phase, result.BuildExitCode, result.ResultPath });
    }
    private static Process StartHost(CompletionBuildOperatorRepository f, string action)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { WorkingDirectory = f.Root,
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var arg in new[] { typeof(CompletionBuildOperatorProbe).Assembly.Location, "completion-build-host", f.Root, f.Source, action })
            info.ArgumentList.Add(arg);
        // Child handoff environment is explicit, with isolated profile, packages and artifacts.
        info.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "PATH", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
            "GIT_CONFIG_NOSYSTEM", "GIT_CONFIG_GLOBAL" })
            if (Environment.GetEnvironmentVariable(key) is { } value) info.Environment[key] = value;
        info.Environment["PATH"] = Path.GetDirectoryName(WorkerShell.Executable) + Path.PathSeparator + info.Environment["PATH"];
        info.Environment["PATHEXT"] = ".COM;.EXE;.BAT;.CMD";
        var profile = Path.Combine(f.Root, "profile");
        Directory.CreateDirectory(profile);
        foreach (var key in new[] { "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "DOTNET_CLI_HOME" }) info.Environment[key] = profile;
        info.Environment["NUGET_PACKAGES"] = Path.Combine(profile, "packages");
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        info.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        info.Environment["MCG_ORCHESTRATOR_HOME"] = f.Source;
        info.Environment[DotnetBuildStorageLayout.IsolatedRootOverrideVariable] = f.Storage;
        var process = Process.Start(info)!;
        _ = process.Handle;
        process.StandardInput.Close();
        // Drain both pipes concurrently; never make the child depend on a tool collecting stdout.
        Drains[process.Id] = Task.WhenAll(Copy(process.StandardOutput, Path.Combine(f.Root, $"host-{action}.out.log")),
            Copy(process.StandardError, Path.Combine(f.Root, $"host-{action}.err.log")));
        return process;
    }
    private static readonly Dictionary<int, Task> Drains = new();
    private static async Task Copy(StreamReader reader, string path)
    {
        await using var output = File.CreateText(path);
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer)) != 0) await output.WriteAsync(buffer.AsMemory(0, count));
    }
    private static async Task Wait(Process process)
    {
        using var guard = new CancellationTokenSource(Guard);
        await process.WaitForExitAsync(guard.Token);
        if (Drains.Remove(process.Id, out var drains)) await drains.WaitAsync(guard.Token);
    }
}
