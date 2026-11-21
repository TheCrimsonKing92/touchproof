using System.Globalization;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal interface IExecutorWholeGateTrialSampler
{
    Task StartAsync(CancellationToken token);
    Task<ExecutorWholeGateTrialMemory> StopAsync();
}

internal sealed class ExecutorWholeGateTrialSshSampler(RemoteLaneExecutorEntry entry, string worktree,
    string trialId, TimeSpan budget,
    Func<string[], string, TimeSpan, CancellationToken, Task<GoalAcceptanceVerifier.CommandResult>>? runCommand = null)
    : IExecutorWholeGateTrialSampler
{
    private CancellationTokenSource? _cancel;
    private Task<GoalAcceptanceVerifier.CommandResult>? _collect;

    public async Task StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _cancel = new();
        var seconds = Math.Ceiling(budget.TotalSeconds).ToString(CultureInfo.InvariantCulture);
        _collect = RunAsync("""
            $elapsed = [Diagnostics.Stopwatch]::StartNew()
            $peak = [long]0
            $limit = [long]0
            $count = 0
            try {
                Add-Type -Name CommitStatus -Namespace Mcg -MemberDefinition @'
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
            public struct MemoryStatus {
                public uint Length, Load;
                public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile;
                public ulong TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
            }
            [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
            private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
            public static MemoryStatus Read() {
                var status = new MemoryStatus { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(MemoryStatus)) };
                if (!GlobalMemoryStatusEx(ref status))
                    throw new System.ComponentModel.Win32Exception();
                return status;
            }
            '@
                while ($elapsed.Elapsed.TotalSeconds -lt BUDGET) {
                    $sample = [Mcg.CommitStatus]::Read()
                    $committed = [long]$sample.TotalPageFile - [long]$sample.AvailablePageFile
                    if ($committed -le 0 -or $sample.TotalPageFile -le 0) { throw 'Commit counters unavailable' }
                    if ($committed -gt $peak) {
                        $peak = $committed
                        $limit = [long]$sample.TotalPageFile
                    }
                    $count++
                    if ($count -eq 1) { [IO.File]::WriteAllText($ready, '') }
                    if ([IO.File]::Exists($stop)) {
                        [pscustomobject]@{ PeakCommittedBytes = $peak; CommitLimitBytesAtPeak = $limit; SampleCount = $count } |
                            ConvertTo-Json -Compress
                        exit 0
                    }
                    $next = [math]::Max(0, 1000 - ($elapsed.ElapsedMilliseconds % 1000))
                    Start-Sleep -Milliseconds $next
                }
                throw 'Commit sampling reached its deadline'
            } finally {
                [IO.File]::Delete($ready)
                [IO.File]::Delete($stop)
            }
            """.Replace("BUDGET", seconds, StringComparison.Ordinal), budget + TimeSpan.FromSeconds(15), _cancel.Token);

        var ready = await RunAsync("""
            $elapsed = [Diagnostics.Stopwatch]::StartNew()
            while (-not [IO.File]::Exists($ready)) {
                if ($elapsed.Elapsed.TotalSeconds -ge 20) { exit 1 }
                Start-Sleep -Milliseconds 200
            }
            """, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
        if (ready.ExitCode != 0 || ready.TimedOut) throw new IOException("executor commit sampler did not become ready");
    }

    public async Task<ExecutorWholeGateTrialMemory> StopAsync()
    {
        if (_collect is null || _cancel is null)
            return ExecutorWholeGateTrialMemory.Unavailable("sampler-not-started");
        using var cancel = _cancel;
        try
        {
            if (!_collect.IsCompleted)
            {
                var stop = await RunAsync("[IO.File]::WriteAllText($stop, '')", TimeSpan.FromSeconds(30),
                    CancellationToken.None).ConfigureAwait(false);
                if (stop.ExitCode != 0 || stop.TimedOut)
                    return ExecutorWholeGateTrialMemory.Unavailable("sampler-stop-unreachable");
            }
            var complete = await _collect.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (complete.ExitCode != 0 || complete.TimedOut)
                return ExecutorWholeGateTrialMemory.Unavailable("sampler-exit-" + complete.ExitCode);
            using var summary = JsonDocument.Parse(complete.Output);
            var value = summary.RootElement;
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty("PeakCommittedBytes", out var peak) && peak.TryGetInt64(out var peakBytes) && peakBytes > 0 &&
                value.TryGetProperty("CommitLimitBytesAtPeak", out var limit) && limit.TryGetInt64(out var limitBytes) && limitBytes > 0 &&
                value.TryGetProperty("SampleCount", out var count) && count.TryGetInt32(out var samples) && samples > 0)
                return new(peakBytes, limitBytes, samples);
            return ExecutorWholeGateTrialMemory.Unavailable("sampler-summary-invalid");
        }
        catch (TimeoutException) { return ExecutorWholeGateTrialMemory.Unavailable("sampler-stop-deadline"); }
        finally
        {
            if (!_collect.IsCompleted) await cancel.CancelAsync().ConfigureAwait(false);
            try { await _collect.ConfigureAwait(false); }
            catch (Exception) { /* The caller observes sampler failure independently of lane outcomes. */ }
        }
    }

    private Task<GoalAcceptanceVerifier.CommandResult> RunAsync(string body, TimeSpan timeout, CancellationToken token)
    {
        // The identifier comes from the trial's GUID, and both SSH commands use the same remote account.
        var id = Guid.ParseExact(trialId, "N").ToString("N");
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $ready = Join-Path ([IO.Path]::GetTempPath()) 'mcg-whole-gate-{{id}}.ready'
            $stop = Join-Path ([IO.Path]::GetTempPath()) 'mcg-whole-gate-{{id}}.stop'
            {{body}}
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        return (runCommand ?? GoalAcceptanceVerifier.RunRemoteLaneTransportAsync)([SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes",
            entry.RunnerAlias!, "powershell.exe", "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            worktree, timeout, token);
    }
}

// Owns only screening receipts and occupancy; never constructs the gate coordinator.
internal static class ExecutorScreeningRunner
{
    internal static async Task<ExecutorScreeningReceipt?> RunAsync(ExecutorScreeningCandidate candidate,
        RemoteLaneExecutorEntry entry, ExecutorScreeningLaneSelection selection,
        RemoteLaneExecutorConfiguration configuration, IRemoteLaneExecutor transport,
        string occupancyRoot, string receiptPath, string receiptId, TimeSpan laneTimeout,
        TimeProvider clock, TimeSpan pollInterval, CancellationToken token = default)
    {
        if (!entry.AdmissionEnabled) return null;
        var attempt = "screening-" + receiptId;
        var claims = new List<IDisposable>();
        var started = clock.GetUtcNow();
        var outcomes = new List<ExecutorScreeningLaneOutcome>();
        var pending = selection.Selected.OrderByDescending(lane => lane.EstimatedSerialSeconds).ToList();
        var running = new List<(AcceptanceTestLane Lane, Task<ExecutorScreeningLaneOutcome> Work)>();
        try
        {
            if (!TryClaimSlots(entry, occupancyRoot, attempt, claims)) return null;
            var unreachable = false;
            while (pending.Count > 0 || running.Count > 0)
            {
                while (running.Count < entry.Slots && pending.Count > 0)
                {
                    var index = pending.FindIndex(lane => !running.Any(active =>
                        lane.ExclusiveResourceKeys.Intersect(active.Lane.ExclusiveResourceKeys, StringComparer.Ordinal).Any()));
                    if (index < 0) break;
                    var lane = pending[index];
                    pending.RemoveAt(index);
                    var request = new RemoteLaneRequest(entry.Id, attempt, candidate.GoalId,
                        ExecutorScreeningPlanner.LaneName(ExecutorScreeningPlanner.Project, lane.Filter),
                        ExecutorScreeningPlanner.Project, lane.Filter, GoalAcceptanceVerifier.ShortHash(lane.Filter),
                        candidate.Commit, candidate.Tree, candidate.MainSha,
                        "screening-" + RemoteFocusedEvidenceShadow.ManifestIdentity(ExecutorScreeningPlanner.Project, lane.Filter, ""));
                    var laneStarted = clock.GetUtcNow();
                    IRemoteLaneHandle? handle = null;
                    if (!unreachable && !token.IsCancellationRequested)
                    {
                        try { handle = (await transport.SubmitAsync(request, token).ConfigureAwait(false)).Handle; }
                        catch (Exception) { /* A failed submission is an observation, never candidate failure. */ }
                    }
                    if (handle is null)
                    {
                        unreachable = true;
                        outcomes.Add(Outcome(lane, request, RemoteLaneOutcomeCode.Unreachable, laneStarted));
                    }
                    else running.Add((lane, PollAsync(lane, request, handle, laneStarted)));
                }
                if (running.Count == 0) continue;
                var done = await Task.WhenAny(running.Select(active => active.Work)).ConfigureAwait(false);
                var completed = running.FindIndex(active => active.Work == done);
                outcomes.Add(await done.ConfigureAwait(false));
                running.RemoveAt(completed);
            }
            var receipt = new ExecutorScreeningReceipt(receiptId, candidate.GoalId, candidate.Commit,
                candidate.Tree, candidate.MainSha, entry.Id, started, clock.GetUtcNow(),
                ExecutorScreeningReceiptLedger.DecideVerdict(outcomes), outcomes, selection.Excluded);
            // Unlike advisory health telemetry, failure to persist this receipt must be visible to the operator.
            ExecutorScreeningReceiptLedger.Append(receiptPath, receipt);
            return receipt;
        }
        finally
        {
            // Poll tasks close their handles before completing. Retain the claims until all have drained.
            try { await Task.WhenAll(running.Select(active => active.Work)).ConfigureAwait(false); }
            finally { ReleaseSlots(entry, occupancyRoot, attempt, claims); }
        }

        ExecutorScreeningLaneOutcome Outcome(AcceptanceTestLane lane, RemoteLaneRequest request,
            RemoteLaneOutcomeCode code, DateTimeOffset at, IReadOnlyList<string>? failing = null,
            IReadOnlyList<string>? ignored = null) => new(lane.Name, request.Lane, request.FilterHash, code,
                failing ?? [], ignored ?? [], Math.Max(0, (clock.GetUtcNow() - at).TotalSeconds));

        async Task<ExecutorScreeningLaneOutcome> PollAsync(AcceptanceTestLane lane, RemoteLaneRequest request,
            IRemoteLaneHandle handle, DateTimeOffset at)
        {
            var observed = false;
            var heartbeat = at;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (handle.NewestHeartbeat is { } newest && newest > heartbeat) heartbeat = newest;
                    if (clock.GetUtcNow() - heartbeat > TimeSpan.FromSeconds(entry.LeaseSeconds))
                        return Outcome(lane, request, RemoteLaneOutcomeCode.LeaseExpired, at);
                    if (clock.GetUtcNow() - at > laneTimeout)
                        return Outcome(lane, request, RemoteLaneOutcomeCode.LaneTimeout, at);
                    if (handle.TryGetResult() is { } result)
                    {
                        observed = true;
                        if (RemoteLaneCoordinator.BindingMismatch(request, result) is { } mismatch)
                            return Outcome(lane, request, mismatch, at);
                        var trx = GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(result.TestResultPaths);
                        var decision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                            new GoalAcceptanceVerifier.CommandResult(result.ExitCode, ""), trx, requireTrxEvidence: true);
                        if (decision.Passed) return Outcome(lane, request, RemoteLaneOutcomeCode.Accepted, at);
                        var red = decision.FailedPredicate is AcceptanceShardCompletionPredicates.FailingTrx or
                            AcceptanceShardCompletionPredicates.AssemblyCleanupFailure or AcceptanceShardCompletionPredicates.NonzeroExit;
                        if (!red) return Outcome(lane, request, RemoteLaneOutcomeCode.TrxIncomplete, at);
                        var identities = GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities(result.TestResultPaths);
                        return Outcome(lane, request, RemoteLaneOutcomeCode.RemoteRed, at,
                            identities.Where(identity => !configuration.Screening.IsKnownDivergent(identity)).ToArray(),
                            identities.Where(configuration.Screening.IsKnownDivergent).ToArray());
                    }
                    if (pollInterval > TimeSpan.Zero) await Task.Delay(pollInterval, token).ConfigureAwait(false);
                    else await Task.Yield();
                }
            }
            catch (Exception) { return Outcome(lane, request, RemoteLaneOutcomeCode.Unreachable, at); }
            finally
            {
                await DrainHandleAsync(handle, observed).ConfigureAwait(false);
            }
        }
    }

    internal static async Task<ExecutorWholeGateTrialReceipt?> RunWholeGateAsync(ExecutorScreeningCandidate candidate,
        RemoteLaneExecutorEntry entry, ExecutorWholeGateTrialPlan plan, RemoteLaneExecutorConfiguration configuration,
        IRemoteLaneExecutor transport, string occupancyRoot, string receiptPath, string receiptId,
        TimeProvider clock, TimeSpan pollInterval, IExecutorWholeGateTrialSampler? sampler,
        Func<DateTimeOffset, ExecutorWholeGateTrialLocalAttempt?>? readLocalAttempt, CancellationToken token = default,
        Func<CancellationToken, Task<bool>>? probe = null, int? maxLanes = null)
    {
        if (!entry.AdmissionEnabled || plan.Lanes.Count == 0 || token.IsCancellationRequested) return null;
        var attempt = "whole-gate-trial-" + receiptId;
        var claims = new List<IDisposable>();
        try
        {
            if (!TryClaimSlots(entry, occupancyRoot, attempt, claims)) return null;
            return await RunClaimedAsync().ConfigureAwait(false);
        }
        finally { ReleaseSlots(entry, occupancyRoot, attempt, claims); }

        async Task<ExecutorWholeGateTrialReceipt> RunClaimedAsync()
        {
            var started = clock.GetUtcNow();
            ExecutorWholeGateTrialLocalAttempt? local = null;
            var memory = ExecutorWholeGateTrialMemory.Unavailable("sampler-unavailable");
            var pending = plan.Lanes.OrderByDescending(lane => lane.EstimatedSerialSeconds).ToList();
            var running = new List<(ExecutorWholeGateTrialLane Lane, Task<TrialExecution> Work)>();
            var outcomes = new List<ExecutorWholeGateTrialLaneResult>();
            var machineLocalKeys = configuration.MachineLocalResourceKeys.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rerunPolicy = new AcceptanceLaneTestFailureRerun(emit: _ => { });
            var settings = AcceptanceHostTestSettings.Overlay(plan.TestSettings, entry.TestSettings);
            using var submissionGate = new SemaphoreSlim(1);
            string? submissionBlocked = null;
            var submissions = 0;
            ExecutorWholeGateTrialReceipt receipt;
            try
            {
                if (sampler is not null)
                {
                    try { await sampler.StartAsync(token).ConfigureAwait(false); }
                    catch (Exception ex) { memory = ExecutorWholeGateTrialMemory.Unavailable("sampler-start: " + ex.Message); }
                }
                local = readLocalAttempt?.Invoke(started);

                while (pending.Count > 0 || running.Count > 0)
                {
                    while (!token.IsCancellationRequested && running.Count < entry.Slots)
                    {
                        var index = pending.FindIndex(lane => running.All(active => MayOverlap(lane, active.Lane)));
                        if (index < 0) break;
                        var lane = pending[index];
                        var laneStarted = clock.GetUtcNow();
                        var request = new RemoteLaneRequest(entry.Id, attempt, candidate.GoalId,
                            ExecutorScreeningPlanner.TrialLaneName(lane, receiptId), lane.Project, lane.Filter,
                            GoalAcceptanceVerifier.ShortHash(lane.Filter), candidate.Commit, candidate.Tree, candidate.MainSha,
                            "whole-gate-trial-" + RemoteFocusedEvidenceShadow.ManifestIdentity(lane.Project, lane.Filter, ""),
                            TestHostTimeoutSeconds: RemoteLaneTestHostBudget.Resolve(lane.Timeout),
                            TestSettings: settings, TestSettingsHost: entry.Id);
                        var submission = await TrySubmitLaneAsync(request).ConfigureAwait(false);
                        if (submission is null) break;
                        pending.RemoveAt(index);
                        if (submission.Handle is { } handle)
                            running.Add((lane, RunLaneWithRerunAsync(lane, request, handle, laneStarted)));
                        else
                            outcomes.Add(new(lane.Name, Elapsed(laneStarted), null, null, [], submission.FailureReason));
                    }

                    if (running.Count == 0) break;
                    var done = await Task.WhenAny(running.Select(item => item.Work)).ConfigureAwait(false);
                    var execution = await done.ConfigureAwait(false);
                    outcomes.Add(execution.Result);
                    running.RemoveAll(item => item.Work == done);
                    // An unconfirmed remote stop cannot free capacity for another machine-local lane.
                    if (!execution.TerminalObserved)
                        Interlocked.CompareExchange(ref submissionBlocked, "executor-completion-unconfirmed", null);
                }
            }
            finally
            {
                foreach (var execution in await Task.WhenAll(running.Select(item => item.Work)).ConfigureAwait(false))
                    outcomes.Add(execution.Result);
                if (sampler is not null)
                {
                    try
                    {
                        var sampled = await sampler.StopAsync().ConfigureAwait(false);
                        if (memory.UnavailableReason == "sampler-unavailable") memory = sampled;
                    }
                    catch (Exception ex) { memory = ExecutorWholeGateTrialMemory.Unavailable("sampler-stop: " + ex.Message); }
                }
                var stopReason = token.IsCancellationRequested ? "cancelled" : submissionBlocked ??
                    (submissions == maxLanes ? "max-lanes-reached" : "trial-interrupted");
                outcomes.AddRange(pending.Select(lane => new ExecutorWholeGateTrialLaneResult(lane.Name, null, null, null, [], stopReason)));
                var status = token.IsCancellationRequested ? ExecutorWholeGateTrialStatus.Cancelled :
                    outcomes.Count != plan.Lanes.Count || outcomes.Any(lane => lane.HasExecutionFailure)
                        ? ExecutorWholeGateTrialStatus.Incomplete : ExecutorWholeGateTrialStatus.Complete;
                receipt = new(receiptId, candidate.GoalId, candidate.Commit, entry.Id, started, Elapsed(started), status,
                    outcomes.OrderBy(lane => lane.LaneName, StringComparer.Ordinal).ToArray(), memory, local, plan.UnsupportedChecks,
                    SubmissionAttempts: submissions);
                ExecutorScreeningReceiptLedger.Append(receiptPath, receipt);
            }
            return receipt;

            bool MayOverlap(ExecutorWholeGateTrialLane lane, ExecutorWholeGateTrialLane active) =>
                !lane.ExclusiveResourceKeys.Intersect(active.ExclusiveResourceKeys, StringComparer.OrdinalIgnoreCase).Any() &&
                !(lane.ExclusiveResourceKeys.Any(machineLocalKeys.Contains) &&
                  active.ExclusiveResourceKeys.Any(machineLocalKeys.Contains));

            async Task<RemoteLaneSubmission?> TrySubmitLaneAsync(RemoteLaneRequest request)
            {
                // Keep admission closed to other submissions while a failed submission is probed.
                await submissionGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (token.IsCancellationRequested || Volatile.Read(ref submissionBlocked) is not null ||
                        submissions == maxLanes) return null;
                    submissions++;
                    var submission = await SubmitTrialLaneAsync(request).ConfigureAwait(false);
                    if (submission.Handle is null && !token.IsCancellationRequested &&
                        !await IsReachableAsync().ConfigureAwait(false))
                        Interlocked.CompareExchange(ref submissionBlocked, "executor-unreachable", null);
                    return submission;
                }
                finally { submissionGate.Release(); }
            }

            async Task<TrialExecution> RunLaneWithRerunAsync(ExecutorWholeGateTrialLane lane, RemoteLaneRequest request,
                IRemoteLaneHandle handle, DateTimeOffset at)
            {
                var first = await ObserveTrialLaneAsync(lane, request, handle, at, 1).ConfigureAwait(false);
                if (lane.RetryCheck is not { } check || first.Check is not { } firstCheck) return first;

                TrialExecution? rerun = null;
                ExecutorWholeGateTrialLaneResult? retryResult = null;
                var rerunFilter = request.Filter;
                var adjudicated = await rerunPolicy.RunAsync(check, firstCheck, async rerunCheck =>
                {
                    if (rerunCheck.FocusedEvidenceTokens.Count > 0)
                        rerunFilter = string.Join('|', rerunCheck.FocusedEvidenceTokens.Select(method => "Name~" + method.Value));
                    var retryRequest = request with
                    {
                        Lane = request.Lane + "-retry",
                        Filter = rerunFilter,
                        FilterHash = GoalAcceptanceVerifier.ShortHash(rerunFilter),
                        ManifestIdentity = "whole-gate-trial-" + RemoteFocusedEvidenceShadow.ManifestIdentity(lane.Project, rerunFilter, "")
                    };
                    var retryStarted = clock.GetUtcNow();
                    var submission = await TrySubmitLaneAsync(retryRequest).ConfigureAwait(false);
                    if (submission?.Handle is { } retryHandle)
                    {
                        rerun = await ObserveTrialLaneAsync(lane, retryRequest, retryHandle, retryStarted, 2).ConfigureAwait(false);
                        retryResult = rerun.Result;
                    }
                    else if (submission is not null)
                        retryResult = new(lane.Name, Elapsed(retryStarted), null, null, [], submission.FailureReason);
                    return rerun?.Check ?? new(lane.Name, false, null, null, TestResultRunOrdinal: 2);
                }, result => result.TestResultRunOrdinal == 1 ? request.Lane : request.Lane + "-retry",
                    cancellationToken: token).ConfigureAwait(false);

                if (retryResult is not { } observed || adjudicated.LaneRerun is not { } evidence) return first;
                // The local comparison reads first-run TRX. Keep those counts comparable and record the rerun separately.
                return new(first.Result with
                {
                    WallSeconds = Elapsed(at),
                    Rerun = new(evidence.Outcome, rerunFilter, observed.WallSeconds, observed.Passed, observed.Failed,
                        observed.FailingTests, observed.ExecutionFailure)
                }, rerun?.TerminalObserved ?? first.TerminalObserved);
            }
        }

        double Elapsed(DateTimeOffset at) => Math.Max(0, (clock.GetUtcNow() - at).TotalSeconds);

        async Task<bool> IsReachableAsync()
        {
            try
            {
                if (probe is not null) return await probe(token).ConfigureAwait(false);
                var result = await GoalAcceptanceVerifier.RunRemoteLaneTransportAsync(
                    [SshRemoteLaneExecutor.SshPath, "-o", "BatchMode=yes", "-o", "ConnectTimeout=10",
                        entry.RunnerAlias!, "powershell.exe", "-NoProfile", "-NonInteractive", "-Command", "exit 0"],
                    candidate.WorktreePath, TimeSpan.FromSeconds(30), token).ConfigureAwait(false);
                return result.ExitCode == 0 && !result.TimedOut;
            }
            catch (Exception) { return false; }
        }

        async Task<RemoteLaneSubmission> SubmitTrialLaneAsync(RemoteLaneRequest request)
        {
            try
            {
                var submission = await transport.SubmitAsync(request, token).ConfigureAwait(false);
                if (submission.Handle is not null) return submission;
                var failedStep = submission.Steps?.LastOrDefault(step => step.TimedOut == true || step.ExitCode is not (null or 0));
                return submission with
                {
                    FailureReason = $"submit-failed:{FirstLine(submission.FailureReason, "refused")}:{FirstLine(failedStep?.StderrTail, "no-detail")}"
                };
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return new(null, "cancelled"); }
            catch (Exception ex) { return new(null, $"submit-failed:{ex.GetType().Name}:{FirstLine(ex.Message, "no-message")}"); }
        }

        static string FirstLine(string? value, string fallback) =>
            value?.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? fallback;

        async Task<TrialExecution> ObserveTrialLaneAsync(ExecutorWholeGateTrialLane lane, RemoteLaneRequest request,
            IRemoteLaneHandle handle, DateTimeOffset at, int ordinal)
        {
            var observed = false;
            var heartbeat = at;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (handle.NewestHeartbeat is { } newest && newest > heartbeat) heartbeat = newest;
                    if (clock.GetUtcNow() - heartbeat > TimeSpan.FromSeconds(entry.LeaseSeconds))
                        return Failure("lease-expired");
                    if (clock.GetUtcNow() - at > lane.Timeout) return Failure("lane-timeout");
                    if (handle.TryGetResult() is { } result)
                    {
                        observed = true;
                        if (RemoteLaneCoordinator.BindingMismatch(request, result) is { } mismatch)
                            return Failure(mismatch.ToString());
                        var paths = result.TestResultPaths;
                        var decision = GoalAcceptanceVerifierTestTelemetry.DecideTestShardCompletion(
                            new GoalAcceptanceVerifier.CommandResult(result.ExitCode, ""),
                            GoalAcceptanceVerifierTestTelemetry.InspectTrxCompletionEvidence(paths),
                            allowEmptySelection: ordinal == 1 && lane.AllowsEmptySelection);
                        var failures = GoalAcceptanceVerifierTestTelemetry.ExtractTrxFailureIdentities(paths);
                        var check = new AcceptanceCheckResult(lane.Name, decision.Passed, result.ExitCode, null,
                            TestResultPaths: paths, TestResultRunOrdinal: ordinal, FailingTestIdentities: failures,
                            CompletionDecision: decision);
                        return new(ExecutorWholeGateTrialLaneResult.ReadTrx(lane.Name, paths, Elapsed(at), decision, failures),
                            observed, check);
                    }
                    if (pollInterval > TimeSpan.Zero) await Task.Delay(pollInterval, token).ConfigureAwait(false);
                    else await Task.Yield();
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return Failure("cancelled"); }
            catch (Exception ex) { return Failure("executor-result: " + ex.Message); }
            finally { await DrainHandleAsync(handle, observed).ConfigureAwait(false); }

            TrialExecution Failure(string reason) => new(new(lane.Name, Elapsed(at), null, null, [], reason), observed);
        }
    }

    private sealed record TrialExecution(ExecutorWholeGateTrialLaneResult Result, bool TerminalObserved,
        AcceptanceCheckResult? Check = null);

    private static bool TryClaimSlots(RemoteLaneExecutorEntry entry, string root, string attempt, List<IDisposable> claims)
    {
        for (var slot = 0; slot < entry.Slots; slot++)
        {
            var claim = RemoteExecutorOccupancy.TryClaimExclusive(root, entry.Id, slot);
            if (claim is null) return false;
            claims.Add(claim);
            RemoteExecutorOccupancy.Claim(root, entry.Id, attempt, slot, RemoteExecutorClaimClass.Screening);
        }
        return true;
    }

    private static void ReleaseSlots(RemoteLaneExecutorEntry entry, string root, string attempt, List<IDisposable> claims)
    {
        for (var slot = claims.Count - 1; slot >= 0; slot--)
        {
            RemoteExecutorOccupancy.Release(root, entry.Id, attempt, slot);
            claims[slot].Dispose();
        }
    }

    private static async Task DrainHandleAsync(IRemoteLaneHandle handle, bool observed)
    {
        try
        {
            if (!observed) (handle as IRemoteLaneQueuedJobCancellation)?.RequestQueuedJobCancellation();
        }
        catch (Exception) { }
        try { handle.Abandon(); } catch (Exception) { }
        if (handle is not SshRemoteLaneHandle ssh) return;

        // Abandon only signals the poller, and queued cancellation runs independently.
        // Neither may outlive our slot claims or the operator command's process.
        try { await ssh.PollLoop.ConfigureAwait(false); } catch (Exception) { }
        try
        {
            if (ssh.QueuedJobCancellation is { } cancellation)
                await cancellation.ConfigureAwait(false);
        }
        catch (Exception) { }
    }
}
