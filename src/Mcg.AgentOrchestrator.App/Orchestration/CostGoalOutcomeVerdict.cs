using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal enum CostGoalOutcomeState { Pending, Met, Missed, InsufficientData }

internal sealed record CostGoalOutcomeResult(int LineIndex, OutcomeMetricLine Line, CostGoalOutcomeState State,
    double? Before, double? After, int BeforeSampleSize, int AfterSampleSize, string? Reason);

internal sealed record CostGoalOutcomeRecord(CostGoalOutcomeFiling Filing,
    IReadOnlyList<CostGoalOutcomeResult> FinalVerdicts, IReadOnlyList<int> EmittedLines,
    string? BacklogNote, bool BacklogApplied);

internal sealed record CostGoalOutcomeBacklogChange(bool CreateFollowUp, string Note);

internal static class CostGoalOutcomeVerdict
{
    internal static IReadOnlyList<CostGoalOutcomeResult> Evaluate(CostGoalOutcomeRecord record,
        IEnumerable<OutcomeGateObservation> gates, string landedHomeCommit, Func<string, string, bool> containsCommit,
        DateTimeOffset adoptedAt, DateTimeOffset evaluatedAt)
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
        var finals = record.FinalVerdicts.ToDictionary(result => result.LineIndex);
        return record.Filing.Baselines.Select((baseline, index) => finals.TryGetValue(index, out var final)
            ? final : EvaluateLine(index, baseline, eligible, evaluatedAt >= deadline)).ToArray();
    }

    private static CostGoalOutcomeResult EvaluateLine(int index, OutcomeMetricBaseline baseline,
        IReadOnlyList<OutcomeGateObservation> eligible, bool expired)
    {
        var line = baseline.Line;
        var values = eligible.Select(gate => CostGoalOutcomeMetric.Sample(line, gate)).OfType<double>()
            .Take(line.Window).ToList();
        double? after = values.Count == line.Window ? CostGoalOutcomeMetric.Aggregate(line, values) : null;
        CostGoalOutcomeResult Result(CostGoalOutcomeState state, string? reason = null) =>
            new(index, line, state, baseline.Value, after, baseline.SampleSize, values.Count, reason);

        var relative = line.Target.EndsWith('%');
        if (relative && baseline.Value is null) return Result(CostGoalOutcomeState.InsufficientData, "baseline-unavailable");
        if (relative && baseline.Value == 0) return Result(CostGoalOutcomeState.InsufficientData, "zero-baseline");
        if (after is null)
            return expired ? Result(CostGoalOutcomeState.InsufficientData, "window-unfilled") :
                Result(CostGoalOutcomeState.Pending);

        var targetText = line.Target.TrimEnd('%');
        var decrease = line.Direction == "decrease";
        var target = relative ? RelativeBound(baseline.Value!.Value, targetText, decrease) :
            double.Parse(targetText, CultureInfo.InvariantCulture);
        var met = decrease ? after.Value <= target : after.Value >= target;
        return Result(met ? CostGoalOutcomeState.Met : CostGoalOutcomeState.Missed);
    }

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
            if (finals.Length > existing.Count)
            {
                // Store the first final results and the first miss's note before delivering side effects.
                var note = record.BacklogNote;
                if (note is null && record.Filing.SourceBacklogItemId is not null &&
                    finals.Any(result => result.State == CostGoalOutcomeState.Missed))
                    note = BacklogNote(goalId, effective);
                record = record with { FinalVerdicts = finals, BacklogNote = note };
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

    private static string Describe(CostGoalOutcomeResult result) =>
        FormattableString.Invariant(
            $"line={result.LineIndex + 1} metric={result.Line.Name} verdict={StateName(result.State)} before={Format(result.Before)} after={Format(result.After)} target={result.Line.Target} direction={result.Line.Direction} window={result.Line.Window} beforeSamples={result.BeforeSampleSize} afterSamples={result.AfterSampleSize} reason={result.Reason ?? "none"}");

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
