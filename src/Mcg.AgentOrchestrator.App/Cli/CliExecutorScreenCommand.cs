using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliExecutorScreenCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals("executor-screen", StringComparison.OrdinalIgnoreCase);

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, OrchestratorWorkspace workspace)
    {
        var root = AcceptancePartitionVerdictCache.ResolveHostStateRoot(workspace.ExecutionDirectory);
        var attemptsDirectory = workspace.OrchestratorDirectory;
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        var cancellable = args.Contains("--whole-gate", StringComparer.Ordinal) || args.Contains("--wait", StringComparer.Ordinal);
        if (cancellable) Console.CancelKeyPress += cancel;
        try
        {
            return await RunAsync(args, RemoteLaneExecutorConfiguration.ResolveStorePath(root),
                () => ReadCandidates(workspace), ReadLanes,
                (configuration, worktree, prefix) => new SshRemoteLaneExecutor(configuration, worktree, prefix,
                    TimeProvider.System, GoalAcceptanceVerifier.RunRemoteLaneTransportAsync, GitCli.Run),
                TimeProvider.System, TimeSpan.FromSeconds(1), root,
                ExecutorScreeningReceiptLedger.ResolveStorePath(root), Console.Out, Console.Error,
                localAttemptSource: (candidate, plan, at) => ReadLocalAttempt(attemptsDirectory, candidate, plan, at),
                samplerFactory: (entry, candidate, id, plan) => new ExecutorWholeGateTrialSshSampler(entry, candidate.WorktreePath, id,
                    TimeSpan.FromMinutes(2 + plan.Lanes.Sum(lane => lane.Timeout.TotalMinutes + 20))),
                token: cancellation.Token,
                passedGateSource: goalId => ReadPassedGates(attemptsDirectory, goalId, cancellation.Token),
                goalIds: () => ReadTrialGoalIds(workspace, attemptsDirectory),
                trialCheckout: gate => OpenTrialCheckout(workspace.ExecutionDirectory, gate, Console.Error)).ConfigureAwait(false);
        }
        finally { if (cancellable) Console.CancelKeyPress -= cancel; }
    }

    internal static async Task<int> RunAsync(IReadOnlyList<string> args, string configurationPath,
        Func<IReadOnlyList<ExecutorScreeningCandidate>> candidateSource,
        Func<string, (IReadOnlyList<AcceptanceTestLane> Lanes, TimeSpan LaneTimeout)> laneSource,
        Func<RemoteLaneExecutorConfiguration, string, string, IRemoteLaneExecutor> executorFactory,
        TimeProvider clock, TimeSpan pollInterval, string occupancyRoot, string receiptPath,
        TextWriter output, TextWriter error,
        Func<string, ExecutorWholeGateTrialPlan>? trialPlanSource = null,
        Func<ExecutorScreeningCandidate, ExecutorWholeGateTrialPlan, DateTimeOffset, ExecutorWholeGateTrialLocalAttempt?>? localAttemptSource = null,
        Func<RemoteLaneExecutorEntry, ExecutorScreeningCandidate, string, ExecutorWholeGateTrialPlan, IExecutorWholeGateTrialSampler>? samplerFactory = null,
        CancellationToken token = default,
        Func<string?, IReadOnlyList<ExecutorPassedGate>>? passedGateSource = null,
        Func<IEnumerable<string>>? goalIds = null,
        Func<ExecutorPassedGate, (ExecutorScreeningCandidate Candidate, IDisposable? Checkout)?>? trialCheckout = null,
        IExecutorSlotOccupancy? occupancy = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        var maximum = 1;
        string? executorId = null;
        string? goalPrefix = null;
        int? remainingLanes = null;
        double? waitMinutes = null;
        var wholeGate = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] is "--help" or "-h") { output.WriteLine(CliCommandHelp.ExecutorScreenUsage); return 0; }
            if (args[i] == "--whole-gate") { wholeGate = true; continue; }
            if (args[i] is not ("--max-candidates" or "--executor" or "--goal" or "--max-lanes" or "--wait") ||
                i + 1 == args.Count || args[i + 1].StartsWith("-", StringComparison.Ordinal)) return Usage();
            var flag = args[i++];
            if (flag == "--executor") { executorId = args[i]; continue; }
            if (flag == "--goal")
            {
                if (string.IsNullOrWhiteSpace(args[i])) return Usage();
                goalPrefix = args[i];
                continue;
            }
            if (flag == "--wait")
            {
                if (!double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var minutes) ||
                    !double.IsFinite(minutes) || minutes < 0) return Usage();
                waitMinutes = minutes;
                continue;
            }
            if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1)
                return Usage();
            if (flag == "--max-lanes") remainingLanes = count;
            else if (count > 50) return Usage();
            else maximum = count;
        }
        if (wholeGate ? string.IsNullOrWhiteSpace(executorId) : goalPrefix is not null || remainingLanes is not null)
            return Usage();
        var configuration = RemoteLaneExecutorConfiguration.Load(configurationPath);
        var refusal = !configuration.Enabled ? "remote-lanes-disabled" : wholeGate ? null : configuration.Screening.FaultReason == "invalid"
            ? "screening-invalid" : configuration.Screening.Mode != "shadow" ? "screening-off" : null;
        if (refusal is not null) { output.WriteLine("SCREEN refused reason=" + refusal); return 2; }
        if (executorId is not null && !configuration.Executors.Any(entry => entry.Id == executorId && entry.Transport == "ssh"))
            return Usage();
        occupancy ??= ExecutorSlotOccupancy.Instance;
        if (wholeGate)
        {
            try { return await RunWholeGateTrialsAsync().ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
        }
        for (var iteration = 0; iteration < maximum; iteration++)
        {
            if (token.IsCancellationRequested) break;
            var entry = PickIdleExecutor();
            if (entry is null) return NoExecutor();
            var candidate = ExecutorScreeningPlanner.PickCandidate(candidateSource(), ExecutorScreeningReceiptLedger.ReadAll(receiptPath));
            if (candidate is null) return Idle("no-candidate");
            var id = Guid.NewGuid().ToString("N");
            var prefix = Path.Combine(occupancyRoot, ".orchestrator", "executor-screening", id, "result");
            try
            {
                var (outcome, waited) = await AcquireAsync<ExecutorScreeningReceipt>(RunAttemptAsync).ConfigureAwait(false);
                if (outcome.Refusal is { } refused) return ReportRefusal(refused, waited);
                var receipt = outcome.Receipt;
                output.WriteLine($"SCREEN goal={candidate.GoalId[..Math.Min(8, candidate.GoalId.Length)]} " +
                    $"commit={candidate.Commit[..Math.Min(9, candidate.Commit.Length)]} executor={entry.Id} " +
                    $"verdict={receipt.Verdict.ToString().ToLowerInvariant()} lanes={receipt.LaneOutcomes.Count} " +
                    $"failing={receipt.LaneOutcomes.Count(lane => lane.FailingTests.Count > 0)} " +
                    $"excluded={receipt.ExcludedLanes.Count} receipt={receipt.ReceiptId}");
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return 0; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { error.WriteLine($"SCREEN error={ex.GetType().Name}: {ex.Message}"); return 1; }

            Task<ExecutorScreeningOutcome<ExecutorScreeningReceipt>> RunAttemptAsync(
                bool isRetry, Func<ExecutorScreeningRefusal?>? acquisitionExpiry)
            {
                var (currentConfiguration, currentEntry) = ReadTarget(entry, isRetry);
                if (CheckTarget(currentEntry, acquisitionExpiry) is { } refused)
                    return Task.FromResult(new ExecutorScreeningOutcome<ExecutorScreeningReceipt>(refused));

                IReadOnlyList<AcceptanceTestLane> lanes = [];
                var timeout = new AcceptanceGateEngineSettings().ResolveCheckTimeout(null);
                try { (lanes, timeout) = laneSource(candidate.WorktreePath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
                { /* An unreadable manifest retires this candidate with an incomplete receipt. */ }
                var selection = ExecutorScreeningPlanner.SelectLanes(lanes, currentConfiguration);
                var transport = executorFactory(currentConfiguration, candidate.WorktreePath, prefix);
                return ExecutorScreeningRunner.RunAsync(candidate, currentEntry, selection, currentConfiguration, transport,
                    occupancyRoot, receiptPath, id, timeout, clock, pollInterval, token, occupancy, acquisitionExpiry);
            }
        }
        return 0;

        async Task<int> RunWholeGateTrialsAsync()
        {
            string? goalId = null;
            if (goalPrefix is not null)
            {
                var matches = (goalIds?.Invoke() ?? []).Where(id => id.StartsWith(goalPrefix, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase).Take(2).ToArray();
                if (matches.Length != 1) return Refuse(matches.Length == 0 ? "goal-unknown" : "goal-ambiguous");
                goalId = matches[0];
            }

            var trials = ExecutorScreeningPlanner.SelectTrialGates(passedGateSource?.Invoke(goalId) ?? [],
                goalId is null ? ExecutorScreeningReceiptLedger.ReadTrials(receiptPath) : [], goalId);
            var completed = 0;
            foreach (var gate in trials)
            {
                if (token.IsCancellationRequested || completed == maximum || remainingLanes == 0) return 0;
                var entry = PickIdleExecutor();
                if (entry is null) return NoExecutor();
                if (trialCheckout?.Invoke(gate) is not { } checkout)
                {
                    if (goalId is not null) return Refuse("candidate-checkout-unavailable");
                    continue;
                }
                using (checkout.Checkout)
                {
                    var result = await RunTrialAsync(checkout.Candidate, entry).ConfigureAwait(false);
                    if (result is { } exit) return exit;
                }
                completed++;
            }
            if (completed > 0) return 0;
            return goalId is null ? Idle("no-eligible-candidates") : Refuse("goal-has-no-passed-gate");
        }

        async Task<int?> RunTrialAsync(ExecutorScreeningCandidate candidate, RemoteLaneExecutorEntry entry)
        {
            var id = Guid.NewGuid().ToString("N");
            var prefix = Path.Combine(occupancyRoot, ".orchestrator", "executor-screening", id, "result");
            try
            {
                var (outcome, waited) = await AcquireAsync<ExecutorWholeGateTrialReceipt>(RunTrialAttemptAsync).ConfigureAwait(false);
                if (outcome.Refusal is { } refused) return ReportRefusal(refused, waited);
                var receipt = outcome.Receipt;
                if (remainingLanes is not null)
                    remainingLanes -= receipt.SubmissionAttempts!.Value;
                output.WriteLine(FormattableString.Invariant(
                    $"TRIAL goal={candidate.GoalId} commit={candidate.Commit} executor={entry.Id} status={receipt.Status.ToString().ToLowerInvariant()} lanes={receipt.Lanes.Count} unsupported-checks={receipt.UnsupportedChecks.Count} wall-seconds={receipt.WallSeconds:F2} receipt={receipt.ReceiptId}"));
                output.WriteLine(ExecutorWholeGateTrialComparison.Format(receipt));
                return null;
            }
            catch (TrialPlanReadException ex)
            {
                output.WriteLine("SCREEN refused reason=invalid-trial-plan");
                error.WriteLine(ex.Message);
                return 2;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            { error.WriteLine($"TRIAL error={ex.GetType().Name}: {ex.Message}"); return 1; }

            Task<ExecutorScreeningOutcome<ExecutorWholeGateTrialReceipt>> RunTrialAttemptAsync(
                bool isRetry, Func<ExecutorScreeningRefusal?>? acquisitionExpiry)
            {
                var (currentConfiguration, currentEntry) = ReadTarget(entry, isRetry);
                if (CheckTarget(currentEntry, acquisitionExpiry) is { } refused)
                    return Task.FromResult(new ExecutorScreeningOutcome<ExecutorWholeGateTrialReceipt>(refused));

                ExecutorWholeGateTrialPlan plan;
                try { plan = trialPlanSource?.Invoke(candidate.WorktreePath) ?? ReadCandidateTrialPlan(candidate); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                    InvalidDataException or ArgumentException or InvalidOperationException)
                { throw new TrialPlanReadException(ex); }
                var transport = executorFactory(currentConfiguration, candidate.WorktreePath, prefix);
                var sampler = samplerFactory?.Invoke(currentEntry, candidate, id, plan);
                return ExecutorScreeningRunner.RunWholeGateAsync(candidate, currentEntry, plan, currentConfiguration, transport,
                    occupancyRoot, receiptPath, id, clock, pollInterval, sampler,
                    at => localAttemptSource?.Invoke(candidate, plan, at), token, maxLanes: remainingLanes,
                    occupancy: occupancy, acquisitionExpiry: acquisitionExpiry);
            }
        }

        async Task<(ExecutorScreeningOutcome<T> Outcome, double? Waited)> AcquireAsync<T>(
            Func<bool, Func<ExecutorScreeningRefusal?>?, Task<ExecutorScreeningOutcome<T>>> run) where T : class
        {
            var started = clock.GetTimestamp();
            token.ThrowIfCancellationRequested();
            var outcome = await run(false, waitMinutes is double minutes && minutes > 0
                ? () => CheckExpiry(minutes)
                : null).ConfigureAwait(false);
            while (waitMinutes is { } budget && outcome.Refusal is { Cause: ExecutorScreeningRefusalCause.SlotBusy } busy)
            {
                token.ThrowIfCancellationRequested();
                var remainingTicks = RemainingTicks(budget);
                if (remainingTicks <= 0) return (outcome, budget);
                var interval = remainingTicks >= pollInterval.Ticks
                    ? pollInterval
                    : TimeSpan.FromTicks((long)Math.Ceiling(remainingTicks));
                if (delay is not null) await delay(interval, token).ConfigureAwait(false);
                else if (interval > TimeSpan.Zero) await Task.Delay(interval, clock, token).ConfigureAwait(false);
                else await Task.Yield();
                token.ThrowIfCancellationRequested();
                if (RemainingTicks(budget) <= 0) return (outcome, budget);
                outcome = await run(true, () => CheckExpiry(budget, busy)).ConfigureAwait(false);
            }
            return (outcome, outcome.Refusal?.Cause == ExecutorScreeningRefusalCause.WaitExpired ? waitMinutes : null);

            double RemainingTicks(double budget) => budget * TimeSpan.TicksPerMinute - clock.GetElapsedTime(started).Ticks;

            ExecutorScreeningRefusal? CheckExpiry(double budget, ExecutorScreeningRefusal? lastBusy = null) =>
                RemainingTicks(budget) > 0 ? null : lastBusy ?? new(ExecutorScreeningRefusalCause.WaitExpired);
        }

        (RemoteLaneExecutorConfiguration Configuration, RemoteLaneExecutorEntry Entry) ReadTarget(
            RemoteLaneExecutorEntry entry, bool retry)
        {
            if (!retry) return (configuration, entry);
            var current = RemoteLaneExecutorConfiguration.Load(configurationPath);
            if (current.DisabledReason == "invalid")
                throw new InvalidDataException($"Invalid remote lane executor configuration: {configurationPath}");
            var refreshed = current.Enabled && (wholeGate || current.Screening.Mode == "shadow")
                ? current.Executors.FirstOrDefault(executor => executor.Id == entry.Id && executor.Transport == "ssh")
                : null;
            return (current, refreshed ?? entry with { AdmissionEnabled = false });
        }

        ExecutorScreeningRefusal? CheckTarget(RemoteLaneExecutorEntry entry, Func<ExecutorScreeningRefusal?>? acquisitionExpiry)
        {
            token.ThrowIfCancellationRequested();
            if (acquisitionExpiry?.Invoke() is { } expired) return expired;
            if (!entry.AdmissionEnabled) return new ExecutorScreeningRefusal(ExecutorScreeningRefusalCause.AdmissionDisabled);
            if (!ExecutorSlotOccupancy.IsValidExecutorId(entry.Id)) throw new InvalidDataException("Invalid executor id.");
            return null;
        }

        int Usage() { error.WriteLine(CliCommandHelp.ExecutorScreenUsage); return 2; }
        int Idle(string reason) { output.WriteLine("SCREEN idle reason=" + reason); return 0; }
        int Refuse(string reason) { output.WriteLine("SCREEN refused reason=" + reason); return 2; }
        int ReportRefusal(ExecutorScreeningRefusal reason, double? waited)
        {
            output.WriteLine(reason.FormatIdleLine(waited));
            return 0;
        }

        RemoteLaneExecutorEntry? PickIdleExecutor() => ExecutorScreeningPlanner.PickIdleExecutor(configuration.Executors,
            id => executorId is null && occupancy.IsOccupied(occupancyRoot, id), executorId)
            ?? (waitMinutes is not null ? ExecutorScreeningPlanner.PickIdleExecutor(configuration.Executors, _ => false, executorId) : null);
        int NoExecutor() => Idle(configuration.Executors.Any(executor => executor.AdmissionEnabled &&
            executor.Transport == "ssh" && (executorId is null || executor.Id == executorId))
                ? "no-idle-executor" : "no-enabled-executor");
    }

    private sealed class TrialPlanReadException(Exception cause) : Exception(cause.Message, cause);

    private static ExecutorWholeGateTrialPlan ReadCandidateTrialPlan(ExecutorScreeningCandidate candidate)
    {
        RequireCandidateWorktree();
        var plan = ReadTrialPlan(candidate.WorktreePath);
        RequireCandidateWorktree();
        return plan;

        void RequireCandidateWorktree()
        {
            if (Scalar(candidate.WorktreePath, "rev-parse", "HEAD") != candidate.Commit)
                throw new InvalidDataException("Trial candidate changed before its lane plan could be read.");
            var status = GitCli.Run(candidate.WorktreePath, GitCli.DefaultTimeoutMilliseconds,
                "status", "--porcelain", "--untracked-files=all");
            if (!status.ProcessStarted || !status.Succeeded || status.DrainTimedOut || !string.IsNullOrWhiteSpace(status.Output))
                throw new InvalidDataException("Trial candidate worktree is unavailable or dirty.");
        }
    }

    internal static ExecutorWholeGateTrialPlan ReadTrialPlan(string worktree)
    {
        const string selectAll = "FullyQualifiedName!=Mcg.NoScannedInfrastructureClasses";
        var settings = AcceptanceGateEngineSettings.Load(worktree);
        var manifest = GoalAcceptanceVerifier.AcceptanceManifest.Load(worktree, changedFiles: null);
        var infrastructure = AcceptanceInfrastructureLaneResolver.ResolveExecution(settings, worktree);
        var declaredChecks = GoalAcceptanceVerifier.ExpandBroadInfrastructureChecks(manifest.Checks, infrastructure.Lanes,
            keepUnshardedInfrastructure: infrastructure.Lanes.Count == 0);
        var lanes = new List<ExecutorWholeGateTrialLane>();
        var unsupported = new List<ExecutorScreeningExcludedLane>();
        foreach (var check in declaredChecks)
        {
            if (!check.Type.Equals("dotnet-test", StringComparison.OrdinalIgnoreCase))
            {
                unsupported.Add(new(check.Name, "executor-transport-requires-dotnet-test"));
                continue;
            }
            if (check.Project is not { Length: > 0 } project || !settings.HasMtpInvocation(project) ||
                !AcceptanceCheckCommandBuilder.UsesMicrosoftTestingPlatform(check))
            {
                unsupported.Add(new(check.Name, "executor-transport-requires-mtp-project"));
                continue;
            }
            var filter = AcceptanceCheckCommandBuilder.ExtractFilterArguments(check.Arguments, []);
            lanes.Add(new(check.Name, project, filter ?? selectAll, check.ExclusiveResourceKeys,
                check.EstimatedSerialSeconds, settings.ResolveCheckTimeout(check.TimeoutMinutes), check.AllowsEmptySelection, check));
        }
        if (lanes.Select(lane => lane.Name).Concat(unsupported.Select(check => check.LaneName))
            .GroupBy(name => name, StringComparer.Ordinal).Any(group => group.Count() > 1))
            throw new InvalidDataException("Trial plan contains duplicate check names.");
        return new(lanes, unsupported, settings.DeclaredTestSettings);
    }

    internal static ExecutorWholeGateTrialLocalAttempt? ReadLocalAttempt(string directory,
        ExecutorScreeningCandidate candidate, ExecutorWholeGateTrialPlan plan, DateTimeOffset trialStarted)
    {
        try
        {
            var metadata = new Dictionary<string, ConductorParallelAcceptanceAttempt>(StringComparer.Ordinal);
            var attempts = ImpactReplayAttemptSource.Read(directory, since: null, skipped: [],
                includeAttempt: (attempt, metadataPath) =>
                {
                    if (candidate.PassedGateAttemptId is { } id &&
                        (attempt.AttemptId != id || attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed))
                        return false;
                    if (attempt.Outcome is not (ConductorParallelAcceptanceAttemptOutcome.Passed or
                            ConductorParallelAcceptanceAttemptOutcome.Failed) ||
                        attempt.CompletedAt is not { } completed || completed > trialStarted ||
                        (candidate.PassedGateAttemptId is null && attempt.BranchHeadSha is { } sha &&
                            sha != candidate.Commit)) return false;
                    metadata.Add(metadataPath, attempt);
                    return true;
                }, goalIds: [candidate.GoalId]);
            var selected = attempts.Where(attempt => attempt.GoalId == candidate.GoalId &&
                    attempt.BranchHeadSha == candidate.Commit && attempt.MetadataPath is not null)
                .OrderByDescending(attempt => metadata[attempt.MetadataPath!].CompletedAt)
                .ThenBy(attempt => attempt.AttemptId, StringComparer.Ordinal).FirstOrDefault();
            if (selected?.MetadataPath is not { } path) return null;
            var completion = metadata[path].CompletedAt!.Value;
            var duration = (completion - selected.StartedAt).TotalSeconds;
            var resultPath = path[..^".attempt.json".Length] + ".result.json";
            var acceptance = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                File.ReadAllText(resultPath), new JsonSerializerOptions(JsonSerializerDefaults.Web))?.Acceptance;
            var laneNames = plan.Lanes.Select(lane => lane.Name).Union(acceptance?.CheckNames ?? [],
                StringComparer.Ordinal).Except(plan.UnsupportedChecks.Select(check => check.LaneName),
                StringComparer.Ordinal);
            var paths = selected.TestResultPaths.Select(raw => Path.GetFullPath(
                raw.Replace('\\', Path.DirectorySeparatorChar), selected.GoalDirectory)).ToArray();
            var localLanes = new List<ExecutorWholeGateTrialLaneResult>();
            foreach (var name in laneNames)
            {
                var storedFailure = acceptance?.UnmetCriteria?.FirstOrDefault(check => !check.Passed && check.Name == name);
                var failedPredicate = storedFailure?.CompletionDecision?.FailedPredicate;
                var slug = AcceptanceArtifactFileNames.Slug(name);
                var pattern = new Regex(@"(?:^|\.)" + Regex.Escape(slug) + @"(?:-run-(\d+))?$",
                    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
                var lanePaths = paths.Select(trx => (Path: trx, Match: pattern.Match(Path.GetFileNameWithoutExtension(trx))))
                    .Where(item => item.Match.Success).GroupBy(item => item.Match.Groups[1].Value)
                    .OrderBy(group => int.TryParse(group.Key, out var ordinal) ? ordinal : 0)
                    .FirstOrDefault()?.Select(item => item.Path).ToArray();
                if (lanePaths is not { Length: > 0 })
                {
                    if (storedFailure is not null || acceptance?.CheckNames?.Contains(name, StringComparer.Ordinal) == true)
                        localLanes.Add(new(name, null, null, null, storedFailure?.FailingTestIdentities ?? [],
                            failedPredicate ?? "local-trx-unavailable"));
                    continue;
                }
                var laneResult = ExecutorWholeGateTrialLaneResult.ReadTrx(name, lanePaths,
                    allowsEmptySelection: plan.Lanes.Any(lane => lane.Name == name && lane.AllowsEmptySelection));
                // Complete TRX counts can coexist with a failed lane process.
                if (storedFailure is not null && (laneResult is { Failed: 0, ExecutionFailure: null } ||
                    failedPredicate is not null and not
                        (AcceptanceShardCompletionPredicates.FailingTrx or AcceptanceShardCompletionPredicates.AssemblyCleanupFailure)))
                {
                    var reason = failedPredicate ?? (storedFailure.ExitCode.GetValueOrDefault() != 0
                        ? AcceptanceShardCompletionPredicates.NonzeroExit : AcceptanceShardCompletionPredicates.CheckFailed);
                    laneResult = laneResult with { ExecutionFailure = reason };
                }
                localLanes.Add(laneResult);
            }
            return new(selected.AttemptId, selected.BranchHeadSha, duration > 0 ? duration : null, localLanes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static (IReadOnlyList<AcceptanceTestLane>, TimeSpan) ReadLanes(string worktree)
    {
        var settings = AcceptanceGateEngineSettings.Load(worktree);
        var check = settings.DeclaredDotnetTestChecks.FirstOrDefault(check => check.Project is { } project &&
            Path.GetFileName(project.Replace('\\', '/')).Equals("Mcg.AgentOrchestrator.Infrastructure.Tests.csproj", StringComparison.OrdinalIgnoreCase));
        return (AcceptanceLaneMembership.ResolveOwnedCollections(settings.InfrastructureTestLanes, worktree),
            settings.ResolveCheckTimeout(check?.TimeoutMinutes));
    }

    internal static IReadOnlyList<ExecutorPassedGate> ReadPassedGates(string directory, string? goalId, CancellationToken token)
    {
        var completionTimes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var attempts = ImpactReplayAttemptSource.Read(directory, since: null, skipped: [], cancellationToken: token,
            includeAttempt: (attempt, path) =>
            {
                if (attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Passed ||
                    attempt.CompletedAt is not { } completed || (goalId is not null && attempt.GoalId != goalId))
                    return false;
                completionTimes[path] = completed;
                return true;
            }, goalIds: goalId is null ? null : [goalId]);
        return attempts.Select(attempt => new ExecutorPassedGate(attempt.GoalId, attempt.AttemptId,
            attempt.BranchHeadSha, attempt.MainHeadSha, completionTimes[attempt.MetadataPath!])).ToArray();
    }

    private static IEnumerable<string> ReadTrialGoalIds(OrchestratorWorkspace workspace, string directory)
    {
        var attempts = Path.Combine(directory, "acceptance-gate-attempts");
        var stored = Directory.Exists(attempts) ? Directory.EnumerateDirectories(attempts).Select(Path.GetFileName) : [];
        return ExperimentGoals.Read(workspace.SqliteStatePath).Select(goal => goal.Id.Value)
            .Concat(stored.OfType<string>());
    }

    private static (ExecutorScreeningCandidate Candidate, IDisposable Checkout)? OpenTrialCheckout(
        string root, ExecutorPassedGate gate, TextWriter error)
    {
        var tree = Scalar(root, "rev-parse", gate.Commit + "^{tree}");
        if (tree is null)
        {
            error.WriteLine($"TRIAL checkout unavailable commit={gate.Commit}");
            return null;
        }
        var parent = OrchestratorTempRoot.GetPurposeDirectory("executor-trial-worktrees");
        Directory.CreateDirectory(parent);
        var path = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        var added = GitCli.RunForRepository(root, "-c", "core.hooksPath=" + Path.Combine(path, "no-hooks"),
            "worktree", "add", "--detach", "--quiet", path, gate.Commit);
        if (!added.ProcessStarted || !added.Succeeded || added.DrainTimedOut)
        {
            error.WriteLine($"TRIAL checkout unavailable commit={gate.Commit} exit={added.ExitCode} {added.Error}");
            return null;
        }
        return (new(gate.GoalId, GoalStatus.Verified, gate.Commit, tree, gate.MainSha, gate.CompletedAt,
            true, true, path, gate.AttemptId), new TrialCheckout(root, path, error));
    }

    private sealed class TrialCheckout(string root, string path, TextWriter error) : IDisposable
    {
        public void Dispose()
        {
            var removed = GitCli.RunForRepository(root, "worktree", "remove", "--force", path);
            if (!removed.ProcessStarted || !removed.Succeeded || removed.DrainTimedOut)
                error.WriteLine($"TRIAL checkout cleanup failed path={path} exit={removed.ExitCode} {removed.Error}");
        }
    }

    private static IReadOnlyList<ExecutorScreeningCandidate> ReadCandidates(OrchestratorWorkspace workspace)
    {
        var main = Scalar(workspace.ExecutionDirectory, "rev-parse", "refs/heads/" + workspace.IntegrationBranch);
        if (main is null) return [];
        var candidates = new List<ExecutorScreeningCandidate>();
        foreach (var goal in ExperimentGoals.Read(workspace.SqliteStatePath))
        {
            if (goal.Status is not (GoalStatus.Active or GoalStatus.Verified)) continue;
            var developers = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer).ToArray();
            if (developers.Length == 0 || developers.Any(task => task.Status != WorkTaskStatus.Completed)) continue;
            var worktree = GoalWorktrees.TryResolve(workspace.ExecutionDirectory, goal.Id);
            if (worktree is null) continue;
            var head = Scalar(worktree, "rev-parse", "HEAD");
            var tree = Scalar(worktree, "rev-parse", "HEAD^{tree}");
            var committed = Scalar(worktree, "log", "-1", "--format=%cI", "HEAD");
            var status = GitCli.Run(worktree, GitCli.DefaultTimeoutMilliseconds, "status", "--porcelain", "--untracked-files=all");
            if (head is null || tree is null || !DateTimeOffset.TryParse(committed, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var at) || !status.ProcessStarted || !status.Succeeded || status.DrainTimedOut) continue;
            candidates.Add(new(goal.Id.Value, goal.Status, head, tree, main, at, string.IsNullOrWhiteSpace(status.Output), true, worktree));
        }
        return candidates;
    }

    private static string? Scalar(string directory, params string[] args)
    {
        var result = GitCli.Run(directory, GitCli.DefaultTimeoutMilliseconds, args);
        return result.ProcessStarted && result.Succeeded && !result.DrainTimedOut && !string.IsNullOrWhiteSpace(result.Output)
            ? result.Output.Trim() : null;
    }
}
