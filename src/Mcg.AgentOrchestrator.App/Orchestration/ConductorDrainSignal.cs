using System.Globalization;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorDrainSignal(string path, TimeProvider clock, bool ignoreInitial)
{
    internal const string FileName = ".conduct-drain";
    internal const double DefaultTimeoutSeconds = 1800;
    internal const string DrainedReason = "drained";
    internal const string TimeoutReason = "drain-timeout";

    private readonly (DateTime LastWrite, DateTime Creation, long Length)? _initialStamp =
        ignoreInitial ? ReadInitialStamp(path) : null;
    private readonly SortedSet<string> _waitedFor = new(StringComparer.Ordinal);
    private readonly HashSet<string> _settledFocusedAttempts = new(StringComparer.Ordinal);
    private long? _started;
    private double _timeoutSeconds;
    private bool _malformed;

    internal bool IsDraining => _started.HasValue;
    private TimeSpan Elapsed => clock.GetElapsedTime(_started!.Value);
    internal bool IsExpired => Elapsed.TotalSeconds >= _timeoutSeconds;

    internal static string PathFor(string stopFilePath) =>
        Path.Combine(Path.GetDirectoryName(stopFilePath) ?? string.Empty, FileName);

    internal static bool TryParseStopArguments(IReadOnlyList<string> args, out bool drain, out double timeoutSeconds)
    {
        drain = false;
        timeoutSeconds = DefaultTimeoutSeconds;
        var hasTimeout = false;
        for (var i = 2; i < args.Count; i++)
        {
            if (!drain && args[i].Equals("--drain", StringComparison.OrdinalIgnoreCase))
                drain = true;
            else if (!hasTimeout && args[i].Equals("--drain-timeout-seconds", StringComparison.OrdinalIgnoreCase)
                && ++i < args.Count &&
                double.TryParse(args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out timeoutSeconds) &&
                IsValidTimeout(timeoutSeconds))
                hasTimeout = true;
            else
                return false;
        }

        return drain || !hasTimeout;
    }

    private static bool IsValidTimeout(double seconds) => double.IsFinite(seconds) && seconds > 0;

    internal static void Write(string path, double timeoutSeconds)
    {
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(new Request(timeoutSeconds)));
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private bool IsPending()
    {
        if (IsDraining)
            return false;
        try
        {
            return ReadStamp(path) is { } stamp && stamp != _initialStamp;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    internal bool Observe()
    {
        if (!IsPending())
            return false;

        _timeoutSeconds = DefaultTimeoutSeconds;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonSerializer.Deserialize<Request>(stream) is { TimeoutSeconds: var seconds } && IsValidTimeout(seconds))
                _timeoutSeconds = seconds;
            else
                _malformed = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _malformed = true;
        }
        _started = clock.GetTimestamp();
        return true;
    }

    internal TimeSpan BoundWait(TimeSpan interval)
    {
        if (!IsDraining)
            return interval;

        var remainingSeconds = _timeoutSeconds - Elapsed.TotalSeconds;
        return remainingSeconds >= interval.TotalSeconds
            ? interval
            : TimeSpan.FromSeconds(Math.Max(0, remainingSeconds));
    }

    internal void Note(Snapshot snapshot) => _waitedFor.UnionWith(snapshot.Identities);

    internal string FormatProgress(int tick, Snapshot snapshot) =>
        $"LOOP_DRAIN tick={tick} {snapshot.Format()}" + (_malformed ? " malformed=true" : string.Empty);

    internal string FormatDrained(int tick) =>
        $"LOOP_DRAINED tick={tick} waited={_waitedFor.Count} identities={string.Join(',', _waitedFor)} elapsedMs={(long)Elapsed.TotalMilliseconds}";

    internal string FormatTimeout(Snapshot snapshot) =>
        $"outcome=timeout fallback=detach {snapshot.Format()} waited={_waitedFor.Count}";

    internal static bool DispatchNeedsReconciliation(TaskSpec task) =>
        task.LastProcess is { } process && (process.IsRunning ||
            task.CompletionBuildEvidence is { IsPending: true } ||
            DispatchProcessCompletionState.IsExitedWithoutAppliedCompletion(task, process));

    internal Snapshot Capture(AgentOrchestratorKernel kernel, ConductorDriver driver, string? onlyGoalId)
    {
        var goals = kernel.Goals.Where(goal => onlyGoalId is null || goal.Id.Value == onlyGoalId).ToArray();
        var goalIds = goals.Select(goal => goal.Id.Value).ToArray();
        var identities = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var goal in goals)
        foreach (var task in goal.Tasks.Where(DispatchNeedsReconciliation))
        {
            var process = task.LastProcess!;
            identities.Add($"dispatch:{goal.Id.Value}:{task.Id.Value}:{process.ProcessId}:{process.StartedAt.UtcTicks}");
        }

        try
        {
            var coordinator = driver.ParallelAcceptanceAttemptCoordinator;
            identities.UnionWith(coordinator.GetUnreconciledAttempts(goalIds)
                .Select(attempt => $"gate:{attempt.GoalId}:{attempt.AttemptId}"));
            identities.UnionWith(driver.GetUnsettledGroupedGateIdentities(onlyGoalId));
            identities.UnionWith(driver.FocusedEvidenceAttemptCoordinator.GetUnreconciledAttempts(goalIds)
                .Where(attempt => !_settledFocusedAttempts.Contains(attempt.AttemptId))
                .Select(attempt => $"evidence:{attempt.GoalId}:{attempt.AttemptId}"));
            return new(identities);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            return new(identities, ex.GetType().Name);
        }
    }

    internal void ReconcileFocusedEvidence(
        ConductorDriver driver, IReadOnlyList<Goal> goals, int tick, Action<string> emit)
    {
        var coordinator = driver.FocusedEvidenceAttemptCoordinator;
        var goalsById = goals.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
        try
        {
            foreach (var attempt in coordinator.GetUnreconciledAttempts(goalsById.Keys))
            {
                if (_settledFocusedAttempts.Contains(attempt.AttemptId))
                    continue;

                var candidate = ConductorParallelAcceptanceCandidate.Create(
                    goalsById[attempt.GoalId], attempt.SlotIndex, attempt.ScopePaths ?? [],
                    attempt.BranchHeadSha, attempt.MainHeadSha);
                var decision = coordinator.ObserveExistingAttempt(attempt, candidate);
                if (decision.Kind is not (ConductorParallelAcceptanceAttemptDecisionKind.Completed or
                    ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun))
                    continue;

                // Leave the durable result in place for the owning finding route to consume.
                // That route can open another evidence arm or dispatch a worker.
                _settledFocusedAttempts.Add(attempt.AttemptId);
                emit($"LOOP_DRAIN_EVIDENCE tick={tick} goal={attempt.GoalId} attempt={attempt.AttemptId} outcome={decision.Attempt.Outcome}");
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException)
        {
            emit($"LOOP_DRAIN_EVIDENCE tick={tick} inflightStateUnavailable=true error={ex.GetType().Name}");
        }
    }

    private static (DateTime LastWrite, DateTime Creation, long Length)? ReadStamp(string path)
    {
        var file = new FileInfo(path);
        try
        {
            return (file.LastWriteTimeUtc, file.CreationTimeUtc, file.Length);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    private static (DateTime LastWrite, DateTime Creation, long Length)? ReadInitialStamp(string path)
    {
        try { return ReadStamp(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    internal sealed record Snapshot(IReadOnlyCollection<string> Identities, string? ReadError = null)
    {
        internal bool IsEmpty => ReadError is null && Identities.Count == 0;

        internal string Format() =>
            $"stillRunning={Identities.Count} identities={string.Join(',', Identities)}" +
            (ReadError is null ? string.Empty : $" inflightStateUnavailable=true error={ReadError}");
    }

    private sealed record Request(double TimeoutSeconds);
}
