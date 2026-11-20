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
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            cancellation.Cancel();
        };
        var trial = args.Contains("--whole-gate", StringComparer.Ordinal);
        if (trial) Console.CancelKeyPress += cancel;
        try
        {
            return await RunAsync(args, RemoteLaneExecutorConfiguration.ResolveStorePath(root),
                () => ReadCandidates(workspace), ReadLanes,
                (configuration, worktree, prefix) => new SshRemoteLaneExecutor(configuration, worktree, prefix,
                    TimeProvider.System, GoalAcceptanceVerifier.RunRemoteLaneTransportAsync, GitCli.Run),
                TimeProvider.System, TimeSpan.FromSeconds(1), root,
                ExecutorScreeningReceiptLedger.ResolveStorePath(root), Console.Out, Console.Error,
                localAttemptSource: (candidate, plan, at) => ReadLocalAttempt(workspace.OrchestratorDirectory, candidate, plan, at),
                samplerFactory: (entry, candidate, id, plan) => new ExecutorWholeGateTrialSshSampler(entry, candidate.WorktreePath, id,
                    TimeSpan.FromMinutes(2 + plan.Lanes.Sum(lane => lane.Timeout.TotalMinutes + 20))),
                token: cancellation.Token).ConfigureAwait(false);
        }
        finally { if (trial) Console.CancelKeyPress -= cancel; }
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
        CancellationToken token = default)
    {
        var maximum = 1;
        string? executorId = null;
        var wholeGate = false;
        for (var i = 1; i < args.Count; i++)
        {
            if (args[i] is "--help" or "-h") { output.WriteLine(CliCommandHelp.ExecutorScreenUsage); return 0; }
            if (args[i] == "--whole-gate") { wholeGate = true; continue; }
            if (args[i] is not ("--max-candidates" or "--executor") || i + 1 == args.Count) return Usage();
            var flag = args[i++];
            if (flag == "--executor") executorId = args[i];
            else if (!int.TryParse(args[i], NumberStyles.None, CultureInfo.InvariantCulture, out maximum) || maximum is < 1 or > 50)
                return Usage();
        }
        if (wholeGate && string.IsNullOrWhiteSpace(executorId)) return Usage();
        var configuration = RemoteLaneExecutorConfiguration.Load(configurationPath);
        var refusal = !configuration.Enabled ? "remote-lanes-disabled" : wholeGate ? null : configuration.Screening.FaultReason == "invalid"
            ? "screening-invalid" : configuration.Screening.Mode != "shadow" ? "screening-off" : null;
        if (refusal is not null) { output.WriteLine("SCREEN refused reason=" + refusal); return 2; }
        if (executorId is not null && !configuration.Executors.Any(entry => entry.Id == executorId && entry.Transport == "ssh"))
            return Usage();
        for (var iteration = 0; iteration < maximum; iteration++)
        {
            if (token.IsCancellationRequested) break;
            var entry = ExecutorScreeningPlanner.PickIdleExecutor(configuration.Executors,
                id => RemoteExecutorOccupancy.IsOccupied(occupancyRoot, id), executorId);
            if (entry is null) return Idle(configuration.Executors.Any(executor => executor.AdmissionEnabled &&
                executor.Transport == "ssh" && (executorId is null || executor.Id == executorId))
                ? "no-idle-executor" : "no-enabled-executor");
            var candidate = wholeGate
                ? ExecutorScreeningPlanner.PickTrialCandidate(candidateSource(), ExecutorScreeningReceiptLedger.ReadTrials(receiptPath))
                : ExecutorScreeningPlanner.PickCandidate(candidateSource(), ExecutorScreeningReceiptLedger.ReadAll(receiptPath));
            if (candidate is null) return Idle("no-candidate");
            if (wholeGate)
            {
                var result = await RunTrialAsync(candidate, entry).ConfigureAwait(false);
                if (result is { } exit) return exit;
                continue;
            }
            IReadOnlyList<AcceptanceTestLane> lanes = [];
            var timeout = new AcceptanceGateEngineSettings().ResolveCheckTimeout(null);
            try { (lanes, timeout) = laneSource(candidate.WorktreePath); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or ArgumentException)
            { /* An unreadable manifest retires this candidate with an incomplete receipt. */ }
            var selection = ExecutorScreeningPlanner.SelectLanes(lanes, configuration);
            var id = Guid.NewGuid().ToString("N");
            var prefix = Path.Combine(occupancyRoot, ".orchestrator", "executor-screening", id, "result");
            try
            {
                var receipt = await ExecutorScreeningRunner.RunAsync(candidate, entry, selection, configuration,
                    executorFactory(configuration, candidate.WorktreePath, prefix), occupancyRoot, receiptPath,
                    id, timeout, clock, pollInterval).ConfigureAwait(false);
                if (receipt is null) return Idle("no-idle-executor");
                output.WriteLine($"SCREEN goal={candidate.GoalId[..Math.Min(8, candidate.GoalId.Length)]} " +
                    $"commit={candidate.Commit[..Math.Min(9, candidate.Commit.Length)]} executor={entry.Id} " +
                    $"verdict={receipt.Verdict.ToString().ToLowerInvariant()} lanes={receipt.LaneOutcomes.Count} " +
                    $"failing={receipt.LaneOutcomes.Count(lane => lane.FailingTests.Count > 0)} " +
                    $"excluded={receipt.ExcludedLanes.Count} receipt={receipt.ReceiptId}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { error.WriteLine($"SCREEN error={ex.GetType().Name}: {ex.Message}"); return 1; }
        }
        return 0;

        async Task<int?> RunTrialAsync(ExecutorScreeningCandidate candidate, RemoteLaneExecutorEntry entry)
        {
            ExecutorWholeGateTrialPlan plan;
            try { plan = trialPlanSource?.Invoke(candidate.WorktreePath) ?? ReadCandidateTrialPlan(candidate); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                InvalidDataException or ArgumentException or InvalidOperationException)
            {
                output.WriteLine("SCREEN refused reason=invalid-trial-plan");
                error.WriteLine(ex.Message);
                return 2;
            }
            if (plan.Lanes.Count == 0)
            {
                output.WriteLine("SCREEN refused reason=empty-trial-plan");
                return 2;
            }
            var id = Guid.NewGuid().ToString("N");
            var prefix = Path.Combine(occupancyRoot, ".orchestrator", "executor-screening", id, "result");
            try
            {
                var receipt = await ExecutorScreeningRunner.RunWholeGateAsync(candidate, entry, plan, configuration,
                    executorFactory(configuration, candidate.WorktreePath, prefix), occupancyRoot, receiptPath, id,
                    clock, pollInterval, samplerFactory?.Invoke(entry, candidate, id, plan),
                    at => localAttemptSource?.Invoke(candidate, plan, at), token).ConfigureAwait(false);
                if (receipt is null) return Idle("no-idle-executor");
                output.WriteLine(FormattableString.Invariant(
                    $"TRIAL goal={candidate.GoalId} commit={candidate.Commit} executor={entry.Id} status={receipt.Status.ToString().ToLowerInvariant()} lanes={receipt.Lanes.Count} unsupported-checks={receipt.UnsupportedChecks.Count} wall-seconds={receipt.WallSeconds:F2} receipt={receipt.ReceiptId}"));
                output.WriteLine(ExecutorWholeGateTrialComparison.Format(receipt));
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { error.WriteLine($"TRIAL error={ex.GetType().Name}: {ex.Message}"); return 1; }
        }

        int Usage() { error.WriteLine(CliCommandHelp.ExecutorScreenUsage); return 2; }
        int Idle(string reason) { output.WriteLine("SCREEN idle reason=" + reason); return 0; }
    }

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
                    if (attempt.Outcome is not (ConductorParallelAcceptanceAttemptOutcome.Passed or
                            ConductorParallelAcceptanceAttemptOutcome.Failed) ||
                        attempt.CompletedAt is not { } completed || completed > trialStarted ||
                        (attempt.BranchHeadSha is { } sha && sha != candidate.Commit)) return false;
                    metadata.Add(metadataPath, attempt);
                    return true;
                }, goalIds: [candidate.GoalId]);
            var selected = attempts.Where(attempt => attempt.BranchHeadSha == candidate.Commit && attempt.MetadataPath is not null)
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
