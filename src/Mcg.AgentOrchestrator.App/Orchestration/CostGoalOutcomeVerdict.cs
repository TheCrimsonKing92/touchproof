using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum CostGoalOutcomeState { Pending, Met, Missed, InsufficientData }

internal sealed record OutcomeGuardResult(OutcomeMetricSample Before, OutcomeMetricSample After);

internal sealed record CostGoalOutcomeResult(int LineIndex, OutcomeMetricLine Line, CostGoalOutcomeState State,
    double? Before, double? After, int BeforeSampleSize, int AfterSampleSize, string? Reason,
    OutcomeGuardResult? Guard = null, int? ReplayAttemptSetSize = null);

internal sealed record CostGoalOutcomeRecord(CostGoalOutcomeFiling Filing,
    IReadOnlyList<CostGoalOutcomeResult> FinalVerdicts, IReadOnlyList<int> EmittedLines,
    string? BacklogNote, bool BacklogApplied)
{
    public IReadOnlyList<CostGoalOutcomeResult> PendingResults { get; init; } = [];
    public CostGoalOutcomeAdoption? Adoption { get; init; }
}

internal sealed record CostGoalOutcomeAdoption(string LandedHomeCommit, string AdoptedCommit, DateTimeOffset AdoptedAt);

internal sealed record CostGoalOutcomeBacklogChange(bool CreateFollowUp, string Note);

internal sealed class CostGoalOutcomeReplay
{
    // Only the fields used by the metrics from ImpactReplayReportWriter's JSON.
    private sealed record Report(Attempt[] Attempts);
    private sealed record Attempt(string AttemptId, string SelectionOutcome, string[] SelectedTestClasses,
        Failure[] FailingClasses, bool MissCheckComplete);
    private sealed record Failure(bool IsMiss);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true
    };

    private readonly Dictionary<string, Attempt> before;
    private readonly Dictionary<string, Attempt> after;

    private CostGoalOutcomeReplay(Dictionary<string, Attempt> before, Dictionary<string, Attempt> after)
    {
        this.before = before;
        this.after = after;
        AttemptSetSize = before.Count == after.Count && before.Keys.All(after.ContainsKey) ? before.Count : null;
    }

    internal int? AttemptSetSize { get; }

    internal bool LostMissCheckCoverage => AttemptSetSize is not null &&
        before.Values.Any(attempt => attempt.MissCheckComplete && !after[attempt.AttemptId].MissCheckComplete);

    internal static CostGoalOutcomeReplay Parse(string beforeJson, string afterJson) =>
        new(Read(beforeJson), Read(afterJson));

    private static Dictionary<string, Attempt> Read(string json)
    {
        var report = JsonSerializer.Deserialize<Report>(json, Json) ??
            throw new JsonException("An impact-replay report is required.");
        var attempts = new Dictionary<string, Attempt>(StringComparer.Ordinal);
        foreach (var attempt in report.Attempts)
            if (attempt is null || string.IsNullOrWhiteSpace(attempt.AttemptId) || !attempts.TryAdd(attempt.AttemptId, attempt))
                throw new JsonException("Impact-replay reports must have a unique, nonempty ID for each attempt.");
        return attempts;
    }

    internal (OutcomeMetricSample Before, OutcomeMetricSample After) Measure(string metric, int window)
    {
        if (AttemptSetSize is null) return (new(null, 0), new(null, 0));
        return (Measure(metric, before.Values, Math.Min(window, 3)), Measure(metric, after.Values, window));
    }

    private static OutcomeMetricSample Measure(string metric, IEnumerable<Attempt> attempts, int minimum)
    {
        // The reports supply a fixed corpus. The window sets the minimum sample size for that corpus.
        if (metric == CostGoalOutcomeMetric.ReplaySelectedClasses)
        {
            var counts = attempts.Where(attempt => attempt.SelectionOutcome == "Resolved")
                .Select(attempt => (double)attempt.SelectedTestClasses.Length).ToList();
            return new(counts.Count >= minimum ? CostGoalOutcomeMetric.Aggregate(metric, counts) : null, counts.Count);
        }
        var checkedAttempts = attempts.Where(attempt => attempt.MissCheckComplete).ToArray();
        return new(checkedAttempts.Length >= minimum
            ? checkedAttempts.Sum(attempt => attempt.FailingClasses.Count(failure => failure.IsMiss)) : null,
            checkedAttempts.Length);
    }
}

internal static class CostGoalOutcomeVerdict
{
    internal static IReadOnlyList<CostGoalOutcomeResult> Evaluate(CostGoalOutcomeRecord record,
        IEnumerable<OutcomeGateObservation> gates, string landedHomeCommit, Func<string, string, bool> containsCommit,
        DateTimeOffset adoptedAt, DateTimeOffset evaluatedAt, CostGoalOutcomeReplay? replay = null,
        IEnumerable<TickStepObservation>? ticks = null)
    {
        if (record.FinalVerdicts.Count == record.Filing.Baselines.Count) return record.FinalVerdicts;
        ArgumentException.ThrowIfNullOrWhiteSpace(landedHomeCommit);
        if (evaluatedAt < adoptedAt) throw new ArgumentOutOfRangeException(nameof(evaluatedAt));
        var deadline = adoptedAt.AddDays(7);
        var eligible = gates.Where(gate => gate.StartedAt >= adoptedAt && gate.StartedAt < deadline &&
                gate.StartedAt <= evaluatedAt && gate.RecordedAt <= evaluatedAt)
            .OrderBy(gate => gate.StartedAt).ThenBy(gate => gate.Id, StringComparer.Ordinal)
            .DistinctBy(gate => gate.Id, StringComparer.Ordinal)
            .Where(gate => !string.IsNullOrWhiteSpace(gate.LaunchMainCommit) &&
                (string.Equals(landedHomeCommit, gate.LaunchMainCommit, StringComparison.OrdinalIgnoreCase) ||
                 containsCommit(landedHomeCommit, gate.LaunchMainCommit)))
            .ToArray();
        var eligibleTicks = (ticks ?? []).Where(tick => tick.Timestamp >= adoptedAt &&
                tick.Timestamp < deadline && tick.Timestamp <= evaluatedAt)
            .OrderBy(tick => tick.Timestamp).ToArray();
        var finals = record.FinalVerdicts.ToDictionary(result => result.LineIndex);
        return record.Filing.Baselines.Select((baseline, index) => finals.TryGetValue(index, out var final)
            ? final : EvaluateLine(index, baseline, eligible, evaluatedAt >= deadline, replay,
                CostGoalOutcomeMetric.TickStepName(baseline.Line.Name) is { } step
                    ? CostGoalOutcomeMetric.MeasureTickStep(baseline.Line, step, eligibleTicks, baseline.Line.Window)
                    : null)).ToArray();
    }

    private static CostGoalOutcomeResult EvaluateLine(int index, OutcomeMetricBaseline baseline,
        IReadOnlyList<OutcomeGateObservation> eligible, bool expired, CostGoalOutcomeReplay? replay,
        OutcomeMetricSample? tickSample)
    {
        var line = baseline.Line;
        var window = new OutcomeGateWindow(line);
        if (window.HasSource)
        {
            foreach (var gate in eligible)
            {
                window.Add(gate);
                if (window.IsFull) break;
            }
        }
        var (gateSample, gateGuard) = window.Measure(line.Window);
        var replayMetric = CostGoalOutcomeMetric.IsReplayMetric(line.Name);
        var replayGuard = CostGoalOutcomeMetric.IsReplayMetric(line.Guard?.Name);
        var usesReplay = replayMetric || replayGuard;
        var (before, after) = replayMetric ? MeasureReplay(line.Name) :
            (new OutcomeMetricSample(baseline.Value, baseline.SampleSize), tickSample ?? gateSample);
        var guard = line.Guard is null ? null : replayGuard
            ? ReplayGuard(line.Guard.Name)
            : new OutcomeGuardResult(baseline.Guard ?? new(null, 0), gateGuard ?? new(null, 0));

        (OutcomeMetricSample Before, OutcomeMetricSample After) MeasureReplay(string metric) =>
            replay?.Measure(metric, line.Window) ?? (new(null, 0), new(null, 0));
        OutcomeGuardResult ReplayGuard(string metric)
        {
            var (guardBefore, guardAfter) = MeasureReplay(metric);
            return new(guardBefore, guardAfter);
        }
        CostGoalOutcomeResult Result(CostGoalOutcomeState state, string? reason = null) =>
            new(index, line, state, before.Value, after.Value, before.SampleSize, after.SampleSize, reason,
                guard, usesReplay ? replay?.AttemptSetSize : null);
        CostGoalOutcomeResult WaitForWindow(string reason) => expired
            ? Result(CostGoalOutcomeState.InsufficientData, reason) : Result(CostGoalOutcomeState.Pending);

        if (usesReplay && replay is { AttemptSetSize: null })
            return Result(CostGoalOutcomeState.InsufficientData, "replay-set-mismatch");
        if (after.Value is null)
            return WaitForWindow(replayMetric && replay is null ? "replay-reports-unavailable" : "window-unfilled");

        var relative = line.Target.EndsWith('%');
        if (relative && before.Value is null) return Result(CostGoalOutcomeState.InsufficientData, "baseline-unavailable");
        if (relative && before.Value == 0) return Result(CostGoalOutcomeState.InsufficientData, "zero-baseline");

        var targetText = line.Target.TrimEnd('%');
        var decrease = line.Direction == "decrease";
        var target = relative ? RelativeBound(before.Value!.Value, targetText, decrease) :
            double.Parse(targetText, CultureInfo.InvariantCulture);
        if (guard?.Before.Value is { } guardBefore && guard.After.Value is { } guardAfter &&
            GuardRegressed(line.Guard!.Rule, guardBefore, guardAfter))
            return Result(CostGoalOutcomeState.Missed, "guard-regressed");
        if (decrease ? after.Value > target : after.Value < target)
            return Result(CostGoalOutcomeState.Missed);
        if (guard is not null)
        {
            if (guard.After.Value is null)
                return WaitForWindow(replayGuard && replay is null ? "replay-reports-unavailable" : "guard-window-unfilled");
            if (guard.Before.Value is null)
                return Result(CostGoalOutcomeState.InsufficientData, "guard-baseline-unavailable");
        }
        if ((line.Name == CostGoalOutcomeMetric.ReplayMissedFailures || line.Guard?.Name == CostGoalOutcomeMetric.ReplayMissedFailures) &&
            replay is { LostMissCheckCoverage: true })
            return Result(CostGoalOutcomeState.InsufficientData, "replay-miss-check-incomplete");
        return Result(CostGoalOutcomeState.Met);
    }

    private static bool GuardRegressed(string rule, double before, double after) => rule switch
    {
        "must-not-increase" => after > before,
        "must-not-decrease" => after < before,
        _ => throw new ArgumentOutOfRangeException(nameof(rule), rule, "Unknown outcome guard rule.")
    };

    private static double RelativeBound(double baseline, string target, bool decrease)
    {
        // Preserve a declared decimal boundary before converting it to the gate observations' scale.
        // A double-only subtraction makes, for example, a 90% decrease from 100 slightly less than 10.
        var culture = CultureInfo.InvariantCulture;
        if (decimal.TryParse(target, culture, out var percent) &&
            decimal.TryParse(baseline.ToString("R", culture), NumberStyles.Float, culture, out var before))
        {
            var fraction = Math.Abs(percent) / 100;
            try { return (double)(before * (decrease ? 1 - fraction : 1 + fraction)); }
            catch (OverflowException) { /* Large numeric declarations still have a double bound. */ }
        }
        var doubleFraction = Math.Abs(double.Parse(target, culture)) / 100;
        return baseline * (decrease ? 1 - doubleFraction : 1 + doubleFraction);
    }

    internal static CostGoalOutcomeState Summarize(IReadOnlyList<CostGoalOutcomeResult> results)
    {
        if (results.Any(result => result.State == CostGoalOutcomeState.Missed)) return CostGoalOutcomeState.Missed;
        if (results.Any(result => result.State == CostGoalOutcomeState.Pending)) return CostGoalOutcomeState.Pending;
        if (results.Count == 0 || results.Any(result => result.State == CostGoalOutcomeState.InsufficientData))
            return CostGoalOutcomeState.InsufficientData;
        return CostGoalOutcomeState.Met;
    }

    internal static CostGoalOutcomeBacklogChange? PlanBacklogChange(GoalId goalId, BacklogItem source,
        IReadOnlyList<CostGoalOutcomeResult> results, string? recordedNote = null)
    {
        if (!results.Any(result => result.State == CostGoalOutcomeState.Missed)) return null;
        return new(source.Status == BacklogItemStatus.Done, recordedNote ?? BacklogNote(goalId, results));
    }

    internal static void Apply(GoalId goalId, IReadOnlyList<CostGoalOutcomeResult> evaluated,
        CostGoalOutcomeStore store, BacklogStore backlog, ConductEventLogWriter events) =>
        store.WithGoalLock(goalId, () =>
        {
            var record = store.Read(goalId) ??
                throw new InvalidOperationException($"Goal '{goalId}' has no stored outcome filing.");
            if (evaluated.Count != record.Filing.Baselines.Count || evaluated.Where((result, index) =>
                    result.LineIndex != index || result.Line != record.Filing.Baselines[index].Line).Any())
                throw new ArgumentException("Outcome results must match the stored metric lines in filing order.", nameof(evaluated));

            var existing = record.FinalVerdicts.ToDictionary(result => result.LineIndex);
            var effective = evaluated.Select(result => existing.GetValueOrDefault(result.LineIndex, result)).ToArray();
            var finals = effective.Where(result => result.State != CostGoalOutcomeState.Pending).ToArray();
            var pending = effective.Where(result => result.State == CostGoalOutcomeState.Pending).ToArray();
            var hasNewFinals = finals.Length > existing.Count;
            if (hasNewFinals || !record.PendingResults.SequenceEqual(pending))
            {
                // Store the first final results and the first miss's note before delivering side effects.
                var note = record.BacklogNote;
                if (hasNewFinals && note is null && record.Filing.SourceBacklogItemId is not null &&
                    finals.Any(result => result.State == CostGoalOutcomeState.Missed))
                    note = BacklogNote(goalId, effective);
                record = record with { FinalVerdicts = finals, PendingResults = pending, BacklogNote = note };
                store.Write(goalId, record);
            }

            foreach (var result in finals.Where(result => !record.EmittedLines.Contains(result.LineIndex)))
            {
                var state = StateName(result.State);
                var eventId = FormattableString.Invariant($"outcome-verdict:{goalId}:{result.LineIndex}:{state}");
                if (!events.AppendRequired("cost-goal-outcome-verdict", goalId.Value,
                        $"OUTCOME_VERDICT goal={goalId} {Describe(result)}", eventId: eventId))
                    throw new IOException($"Could not append outcome verdict '{eventId}'.");
                record = record with { EmittedLines = [.. record.EmittedLines, result.LineIndex] };
                store.Write(goalId, record);
            }

            if (!record.BacklogApplied && record.BacklogNote is { } backlogNote &&
                record.Filing.SourceBacklogItemId is { } sourceId)
            {
                ApplyBacklogChange(goalId, sourceId, backlogNote, effective, backlog);
                store.Write(goalId, record with { BacklogApplied = true });
            }
        });

    private static void ApplyBacklogChange(GoalId goalId, string sourceId, string recordedNote,
        IReadOnlyList<CostGoalOutcomeResult> results, BacklogStore backlog)
    {
        var source = backlog.GetByExactIdAsync(sourceId).GetAwaiter().GetResult() ??
            throw new InvalidOperationException($"Outcome verdict source backlog item '{sourceId}' is missing.");
        var change = PlanBacklogChange(goalId, source, results, recordedNote) ??
            throw new InvalidOperationException($"Goal '{goalId}' has an outcome note without a missed verdict.");
        var note = change.Note;
        if (source.Notes.Any(entry => entry.Text == note)) return;

        var followUpId = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"cost-goal-outcome:{goalId}:{sourceId}")).AsSpan(0, 16));
        var followUp = backlog.GetByExactIdAsync(followUpId).GetAwaiter().GetResult();
        if (followUp is null && !change.CreateFollowUp)
        {
            if (source.Status == BacklogItemStatus.Superseded)
                backlog.UnsupersedeAsync(sourceId).GetAwaiter().GetResult();
            var update = backlog.TryRecordOpenItemLandingAsync(sourceId, note).GetAwaiter().GetResult();
            if (update.Disposition is BacklogLandingDisposition.Recorded or BacklogLandingDisposition.AlreadyRecorded) return;
            if (update.Item?.Status != BacklogItemStatus.Done)
                throw new InvalidOperationException($"Outcome verdict source backlog item '{sourceId}' could not be reopened.");
        }

        if (followUp is null)
        {
            var now = DateTimeOffset.UtcNow;
            backlog.UpsertAsync(new(followUpId, $"Follow up cost outcome for {source.Title}",
                $"Source backlog item: {sourceId}\n\n{note}", BacklogItemStatus.Open, now, now, goalId.Value, Tags: source.Tags))
                .GetAwaiter().GetResult();
        }
        backlog.LinkAsync(sourceId, followUpId, BacklogLinkKind.Related).GetAwaiter().GetResult();
    }

    private static string BacklogNote(GoalId goalId, IReadOnlyList<CostGoalOutcomeResult> results) =>
        $"OUTCOME_VERDICT goal={goalId}\n" + string.Join('\n', results.Select(Describe));

    private static string Describe(CostGoalOutcomeResult result)
    {
        var detail = FormattableString.Invariant(
            $"line={result.LineIndex + 1} metric={result.Line.Name} verdict={StateName(result.State)} before={Format(result.Before)} after={Format(result.After)} target={result.Line.Target} direction={result.Line.Direction} window={result.Line.Window} beforeSamples={result.BeforeSampleSize} afterSamples={result.AfterSampleSize} reason={result.Reason ?? "none"}");
        if (result.Line.Guard is { } declaration && result.Guard is { } guard)
            detail += FormattableString.Invariant(
                $" guard={declaration.Name} guardRule={declaration.Rule} guardBefore={Format(guard.Before.Value)} guardAfter={Format(guard.After.Value)} guardBeforeSamples={guard.Before.SampleSize} guardAfterSamples={guard.After.SampleSize}");
        if (result.ReplayAttemptSetSize is { } size)
            detail += FormattableString.Invariant($" replayAttemptSetSize={size}");
        return detail;
    }

    private static string Format(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "unavailable";

    private static string StateName(CostGoalOutcomeState state) => state switch
    {
        CostGoalOutcomeState.Pending => "pending",
        CostGoalOutcomeState.Met => "met",
        CostGoalOutcomeState.Missed => "missed",
        CostGoalOutcomeState.InsufficientData => "insufficient-data",
        _ => throw new ArgumentOutOfRangeException(nameof(state))
    };
}
