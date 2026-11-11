using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

public sealed record ProviderBudgetHoldSchedule(
    ProviderBudgetExhaustionHold Hold, int Attempt, DateTimeOffset? ResetAt, string ResetSource,
    DateTimeOffset NextProbeAt, bool ProbeInFlight, GoalId? AdmittedGoalId, TaskId? AdmittedTaskId,
    string LastProbeOutcome)
{
    public DateTimeOffset? LastProbeAt { get; init; }
    public string? ResetIntentId { get; init; }
    public ProviderFailureKind LastActionableFailure { get; init; }
    public DateTimeOffset? LastActionableFailureAt { get; init; }
}

/// <summary>A schedule is a projection of binding history; admission notes reserve one real task dispatch.</summary>
public static class ProviderBudgetHoldSchedules
{
    public sealed record ProbeMarker(string BindingKey, DateTimeOffset Due, int Attempt, string ResetSource)
    {
        public string? ResetIntentId { get; init; }
        public DateTimeOffset? HoldPlacedAt { get; init; }
    }
    public sealed record ResetMarker(string BindingKey, DateTimeOffset ResetAt, string IntentId)
    {
        public DateTimeOffset? IssuedAt { get; init; }
    }
    public sealed record ResetGeneration(ResetMarker Marker, DateTimeOffset AppliedAt);
    public sealed record StandaloneResult(string BindingKey, DateTimeOffset PlacedAt, DateTimeOffset Due,
        int Attempt, DateTimeOffset StartedAt, string Outcome, string Reason)
    {
        public string? ResetIntentId { get; init; }
        public ProviderFailureKind FailureKind => ClassifyOutcome(Outcome);
    }

    public static ProviderFailureKind ClassifyOutcome(string outcome) => outcome switch
    {
        "authentication-required" => ProviderFailureKind.AuthenticationRequired,
        "exhausted" => ProviderFailureKind.BudgetExhausted,
        _ => ProviderFailureKind.Unknown
    };

    // The persisted intent is the generation authority. Replayed notes cannot refresh it.
    public static ResetGeneration? LatestReset(IEnumerable<Goal> goals, string bindingKey,
        DateTimeOffset? asOf = null, ProgressEvent? beforeEvent = null) =>
        goals.SelectMany(goal => beforeEvent is null || goal.Id != beforeEvent.GoalId
            ? goal.Timeline : goal.Timeline.TakeWhile(evt => evt != beforeEvent))
            .Where(evt => (asOf is null || evt.OccurredAt <= asOf) &&
                TryParseReset(evt, out var reset) && SameBinding(reset.BindingKey, bindingKey))
            .Select(evt => { TryParseReset(evt, out var reset); return new ResetGeneration(reset, evt.OccurredAt); })
            .GroupBy(reset => reset.Marker.IntentId, StringComparer.Ordinal)
            .Select(group => group.MinBy(reset => reset.AppliedAt)!)
            .OrderBy(reset => reset.Marker.IssuedAt ?? reset.AppliedAt).ThenBy(reset => reset.AppliedAt).LastOrDefault();

    public static bool IsApplicable(StandaloneResult result, ResetGeneration? reset) =>
        reset is null ? result.ResetIntentId is null :
        result.StartedAt >= reset.AppliedAt && result.StartedAt >= reset.Marker.ResetAt &&
        (result.ResetIntentId == reset.Marker.IntentId || result.ResetIntentId is null && result.StartedAt > reset.AppliedAt);

    public static bool IsCurrentDispatch(IEnumerable<Goal> goals, string bindingKey, TaskSpec task,
        TaskDispatchRecord dispatch, DateTimeOffset? asOf = null)
    {
        var all = goals.ToArray();
        var parsed = DispatchMarker(all, bindingKey, task, dispatch);
        if (!MatchesGeneration(all, bindingKey, task, dispatch, asOf)) return false;
        if (parsed?.HoldPlacedAt is not { } placed) return true;

        // Validate exhaustion observations in time order. A rejected old-generation
        // result cannot create a newer hold that poisons a subsequent current probe.
        // Timestamp ties are evaluated together, without inventing an ordering.
        DateTimeOffset? latestExhaustion = null;
        var observations = all.SelectMany(goal => goal.Tasks).SelectMany(owner => owner.VerificationHistory
                .Where(verification => verification.ProviderFailureKind == ProviderFailureKind.BudgetExhausted &&
                    (asOf is null || verification.CompletedAt <= asOf))
                .Select(verification => (Task: owner, Verification: verification,
                    Dispatch: SubscriptionLimitEvidence.FindDispatchForVerification(owner, verification))))
            .Where(item => item.Dispatch is not null && IsBinding(item.Dispatch, bindingKey))
            .GroupBy(item => item.Verification.CompletedAt).OrderBy(group => group.Key);
        foreach (var group in observations)
        {
            var applicable = group.Where(item => MatchesGeneration(all, bindingKey, item.Task,
                    item.Dispatch!, item.Verification.CompletedAt) &&
                (DispatchMarker(all, bindingKey, item.Task, item.Dispatch!)?.HoldPlacedAt is not { } original ||
                    latestExhaustion is null || latestExhaustion <= original)).ToArray();
            if (applicable.Length == 0) continue;
            if (group.Key > placed && applicable.Any(item => item.Task != task || item.Dispatch != dispatch)) return false;
            latestExhaustion = group.Key;
        }
        return true;
    }

    private static ProbeMarker? DispatchMarker(IReadOnlyCollection<Goal> all, string bindingKey,
        TaskSpec task, TaskDispatchRecord dispatch)
    {
        var marker = all.SelectMany(goal => goal.Timeline).Where(evt => evt.TaskId == task.Id && evt.OccurredAt <= dispatch.DispatchedAt &&
                TryParseProbe(evt, out var probe) && SameBinding(probe.BindingKey, bindingKey))
            .OrderBy(evt => evt.OccurredAt).LastOrDefault();
        return marker is not null && TryParseProbe(marker, out var found) ? found : null;
    }

    private static bool MatchesGeneration(IReadOnlyCollection<Goal> all, string bindingKey, TaskSpec task,
        TaskDispatchRecord dispatch, DateTimeOffset? asOf)
    {
        var reset = LatestReset(all, bindingKey, asOf);
        var marker = all.SelectMany(goal => goal.Timeline).Where(evt => evt.TaskId == task.Id && evt.OccurredAt <= dispatch.DispatchedAt &&
                TryParseProbe(evt, out var probe) && SameBinding(probe.BindingKey, bindingKey))
            .OrderBy(evt => evt.OccurredAt).LastOrDefault();
        ProbeMarker? parsed = marker is not null && TryParseProbe(marker, out var found) ? found : null;
        if (reset is null) return parsed?.ResetIntentId is null;
        if (dispatch.DispatchedAt < reset.AppliedAt || dispatch.DispatchedAt < reset.Marker.ResetAt) return false;
        return parsed is null || parsed.ResetIntentId == reset.Marker.IntentId ||
            parsed.ResetIntentId is null && marker!.OccurredAt > reset.AppliedAt;
    }

    public static bool IsStaleProbeResult(IEnumerable<Goal> goals, string bindingKey, TaskSpec task,
        TaskDispatchRecord dispatch, DateTimeOffset completedAt)
    {
        var all = goals.ToArray();
        return all.SelectMany(goal => goal.Timeline).Any(evt => evt.TaskId == task.Id &&
                evt.OccurredAt <= dispatch.DispatchedAt && TryParseProbe(evt, out var marker) && SameBinding(marker.BindingKey, bindingKey)) &&
            !IsCurrentDispatch(all, bindingKey, task, dispatch, completedAt);
    }

    public static string FormatStandaloneResult(StandaloneResult result) =>
        $"PROVIDER_BUDGET_CHECK binding={Uri.EscapeDataString(result.BindingKey)} placed_at={FormatInstant(result.PlacedAt)} due={FormatInstant(result.Due)} attempt={result.Attempt} started_at={FormatInstant(result.StartedAt)} outcome={result.Outcome} reason={Uri.EscapeDataString(result.Reason)}" +
        (result.ResetIntentId is { } intent ? $" reset_intent={Uri.EscapeDataString(intent)}" : "");

    public static bool TryParseStandaloneResult(ProgressEvent evt, out StandaloneResult result)
    {
        result = null!;
        if (evt.Kind != ProgressKind.TaskNote || !evt.Message.StartsWith("PROVIDER_BUDGET_CHECK ", StringComparison.Ordinal)) return false;
        var fields = evt.Message.Split(' ');
        if (fields.Length is not (8 or 9)) return false;
        string Value(int index, string key) => fields[index].StartsWith(key + "=", StringComparison.Ordinal)
            ? fields[index][(key.Length + 1)..] : "";
        if (!TryParseInstant(Value(2, "placed_at"), out var placed) || !TryParseInstant(Value(3, "due"), out var due) ||
            !int.TryParse(Value(4, "attempt"), out var attempt) || attempt < 1 ||
            !TryParseInstant(Value(5, "started_at"), out var started) || started < due || started < placed ||
            evt.OccurredAt < started || Value(6, "outcome") is not ("available" or "exhausted" or "failed" or "authentication-required") ||
            fields.Length == 9 && Value(8, "reset_intent").Length == 0 ||
            Value(1, "binding").Length == 0) return false;
        result = new(Uri.UnescapeDataString(Value(1, "binding")), placed, due, attempt, started,
            Value(6, "outcome"), Uri.UnescapeDataString(Value(7, "reason")))
            { ResetIntentId = fields.Length == 9 ? Uri.UnescapeDataString(Value(8, "reset_intent")) : null };
        return true;
    }

    private static readonly Regex ProbePattern = new(
        @"\APROVIDER_BUDGET_PROBE binding=(?<binding>.+) due=(?<at>\S+) attempt=(?<attempt>\d+) reset_source=(?<source>operator|parsed|backoff)(?: reset_intent=(?<intent>\S+))?(?: placed_at=(?<placed>\S+))?\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ResetPattern = new(
        @"\APROVIDER_BUDGET_RESET binding=(?<binding>.+) reset_at=(?<at>\S+) source=operator intent=(?<intent>\S+)\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex IsoResetPattern = new(
        @"\bresets (?:at|on) (?<at>\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2}))(?![\w:+-])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CalendarResetPattern = new(
        @"\bresets (?<month>[A-Za-z]{3}) (?<day>\d{1,2}), (?<hour>\d{1,2})(?::(?<minute>\d{2}))?(?<period>am|pm) \((?<zone>[^)]+)\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static string FormatInstant(DateTimeOffset instant) => instant.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    public static string FormatProbe(ProbeMarker marker) =>
        $"PROVIDER_BUDGET_PROBE binding={marker.BindingKey} due={FormatInstant(marker.Due)} attempt={marker.Attempt} reset_source={marker.ResetSource}" +
        (marker.ResetIntentId is { } intent ? $" reset_intent={Uri.EscapeDataString(intent)}" : "") +
        (marker.HoldPlacedAt is { } placed ? $" placed_at={FormatInstant(placed)}" : "");
    public static string FormatReset(ResetMarker marker) =>
        $"PROVIDER_BUDGET_RESET binding={marker.BindingKey} reset_at={FormatInstant(marker.ResetAt)} source=operator intent={marker.IntentId}";

    public static bool TryParseProbe(ProgressEvent evt, out ProbeMarker marker)
    {
        marker = null!;
        if (evt.Kind != ProgressKind.TaskNote) return false;
        var match = ProbePattern.Match(evt.Message);
        if (!match.Success || !TryParseInstant(match.Groups["at"].Value, out var at) ||
            !int.TryParse(match.Groups["attempt"].Value, out var attempt) || attempt < 1) return false;
        if (match.Groups["placed"].Success && !TryParseInstant(match.Groups["placed"].Value, out _)) return false;
        marker = new(match.Groups["binding"].Value, at, attempt, match.Groups["source"].Value)
        {
            ResetIntentId = match.Groups["intent"].Success ? Uri.UnescapeDataString(match.Groups["intent"].Value) : null,
            HoldPlacedAt = match.Groups["placed"].Success ? DateTimeOffset.Parse(match.Groups["placed"].Value, CultureInfo.InvariantCulture) : null
        };
        return true;
    }

    public static bool TryParseReset(ProgressEvent evt, out ResetMarker marker)
    {
        marker = null!;
        if (evt.Kind != ProgressKind.TaskNote) return false;
        var match = ResetPattern.Match(evt.Message);
        if (!match.Success || !TryParseInstant(match.Groups["at"].Value, out var at)) return false;
        marker = new(match.Groups["binding"].Value, at, match.Groups["intent"].Value)
            { IssuedAt = evt.OperatorIntentCreatedAt };
        return true;
    }

    public static bool TryParseInstant(string text, out DateTimeOffset instant)
    {
        instant = default;
        if (!Regex.IsMatch(text, @"\A\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:\d{2})\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant) ||
            !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant)) return false;
        instant = instant.ToUniversalTime();
        return true;
    }

    public static bool TryGetResetAt(TaskVerificationRecord exhaustion, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (!ProviderLimitSignals.TryGetProviderBudgetExhaustionLine(exhaustion, out var line)) return false;
        if (ProviderLimitSignals.TryGetRelativeRetryAfter(line, out var delay))
        {
            try { resetAt = exhaustion.CompletedAt.Add(delay).ToUniversalTime(); }
            catch (ArgumentOutOfRangeException) { return false; }
            return resetAt > exhaustion.CompletedAt;
        }
        var iso = IsoResetPattern.Match(line);
        if (iso.Success && TryParseInstant(iso.Groups["at"].Value, out resetAt))
            return resetAt > exhaustion.CompletedAt;
        var match = CalendarResetPattern.Match(line);
        if (!match.Success) return false;
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups["zone"].Value);
            var year = TimeZoneInfo.ConvertTime(exhaustion.CompletedAt, zone).Year;
            var month = DateTime.ParseExact(match.Groups["month"].Value, "MMM", CultureInfo.InvariantCulture).Month;
            var hour = int.Parse(match.Groups["hour"].Value, CultureInfo.InvariantCulture);
            if (hour is < 1 or > 12) return false;
            hour = hour % 12 + (match.Groups["period"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase) ? 12 : 0);
            var minute = match.Groups["minute"].Success ? int.Parse(match.Groups["minute"].Value, CultureInfo.InvariantCulture) : 0;
            var day = int.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture);
            var candidates = new List<DateTimeOffset>();
            foreach (var candidateYear in new[] { year - 1, year, year + 1 })
            {
                if (candidateYear is < 1 or > 9999 || day < 1 || day > DateTime.DaysInMonth(candidateYear, month)) continue;
                var local = new DateTime(candidateYear, month, day, hour, minute, 0);
                if (!zone.IsInvalidTime(local) && !zone.IsAmbiguousTime(local))
                    candidates.Add(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, zone)));
            }
            if (candidates.Count == 0) return false;
            resetAt = candidates.MinBy(at => (at - exhaustion.CompletedAt).Duration());
            return resetAt > exhaustion.CompletedAt;
        }
        catch (Exception exception) when (exception is ArgumentException or TimeZoneNotFoundException or InvalidTimeZoneException or FormatException)
        {
            return false;
        }
    }

    public static bool TryGetHoldByBindingKey(IEnumerable<Goal> goals, string bindingKey, out ProviderBudgetExhaustionHold hold)
    {
        var all = goals.ToArray();
        return TryGetHoldByBindingKey(all, bindingKey, out hold, new ProviderBudgetHoldMemo(all));
    }

    internal static bool TryGetHoldByBindingKey(IReadOnlyCollection<Goal> all, string bindingKey,
        out ProviderBudgetExhaustionHold hold, ProviderBudgetHoldMemo memo)
    {
        foreach (var dispatch in all.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory))
            if (IsBinding(dispatch, bindingKey) && memo.TryGet(
                    dispatch.ProviderName ?? dispatch.WorkerName, dispatch.ClaudeCredentialSourceDirectory, out hold))
                return true;
        hold = null!;
        return false;
    }

    public static ProviderBudgetHoldSchedule Evaluate(IEnumerable<Goal> goals, ProviderBudgetExhaustionHold hold, DateTimeOffset now)
    {
        var all = goals.ToArray();
        var tasks = all.SelectMany(goal => goal.Tasks).ToArray();
        var dispatches = tasks.SelectMany(task => task.DispatchHistory.Where(dispatch => IsBinding(dispatch, hold.BindingKey))
            .Select(dispatch => (Task: task, Dispatch: dispatch))).ToArray();
        var observations = tasks.SelectMany(task => task.VerificationHistory.Select(verification =>
            (Task: task, Verification: verification, Dispatch: SubscriptionLimitEvidence.FindDispatchForVerification(task, verification))))
            .Where(item => item.Dispatch is not null && IsBinding(item.Dispatch, hold.BindingKey) &&
                !IsStaleProbeResult(all, hold.BindingKey, item.Task, item.Dispatch, item.Verification.CompletedAt)).ToArray();
        var exhaustion = observations.Where(item => item.Verification.ProviderFailureKind == ProviderFailureKind.BudgetExhausted)
            .MaxBy(item => item.Verification.CompletedAt).Verification
            ?? throw new InvalidOperationException("A budget hold requires an exhaustion verification on its binding.");
        var notes = all.SelectMany(goal => goal.Timeline.Select((evt, index) => (Goal: goal, Event: evt, Index: index))).ToArray();
        // Preserve historical recovery even after a later exhaustion or reset. Each
        // receipt must have been applicable when observed, on its original source.
        var standaloneHistory = notes.Where(item => TryParseStandaloneResult(item.Event, out var result) &&
            SameBinding(result.BindingKey, hold.BindingKey) &&
            observations.Any(observation => observation.Task.Id == item.Event.TaskId &&
                observation.Verification.ProviderFailureKind == ProviderFailureKind.BudgetExhausted &&
                observation.Verification.CompletedAt == result.PlacedAt) &&
            IsApplicable(result, LatestReset(all, hold.BindingKey, item.Event.OccurredAt, item.Event)))
            .OrderBy(item => item.Event.OccurredAt).ToArray();
        var recoveryAt = tasks.Where(task => task.LatestProviderBudgetRecoveryAt is not null)
            .Where(task => task.DispatchHistory.LastOrDefault(dispatch => dispatch.DispatchedAt <= task.LatestProviderBudgetRecoveryAt) is { } dispatch && IsBinding(dispatch, hold.BindingKey))
            .Select(task => task.LatestProviderBudgetRecoveryAt!.Value)
            .Concat(observations.Where(item => ProviderLimitSignals.HasAvailabilityEvidence(item.Verification) &&
                IsCurrentDispatch(all, hold.BindingKey, item.Task, item.Dispatch!, item.Verification.CompletedAt))
                .Select(item => item.Verification.CompletedAt))
            .Concat(standaloneHistory.Where(item => TryParseStandaloneResult(item.Event, out var result) && result.Outcome == "available")
                .Select(item => item.Event.OccurredAt))
            .Where(at => at <= exhaustion.CompletedAt)
            .DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var reset = LatestReset(all, hold.BindingKey);
        var firstExhaustion = observations.Where(item => item.Verification.ProviderFailureKind == ProviderFailureKind.BudgetExhausted &&
            item.Verification.CompletedAt >= recoveryAt).Min(item => item.Verification.CompletedAt);
        if (reset?.AppliedAt < firstExhaustion) reset = null;
        standaloneHistory = standaloneHistory.Where(item => TryParseStandaloneResult(item.Event, out var result) &&
            result.PlacedAt >= firstExhaustion).ToArray();
        var standalone = standaloneHistory.Where(item => TryParseStandaloneResult(item.Event, out var result) &&
            IsApplicable(result, reset)).ToArray();
        var probes = notes.Where(item => TryParseProbe(item.Event, out var marker) && SameBinding(marker.BindingKey, hold.BindingKey))
            .OrderBy(item => item.Event.OccurredAt).ToArray();
        var latestProbe = probes.LastOrDefault();
        var probeResults = probes.Select(probe =>
            {
                var task = probe.Goal.Tasks.FirstOrDefault(task => task.Id == probe.Event.TaskId);
                var dispatch = task?.DispatchHistory.FirstOrDefault(dispatch => IsBinding(dispatch, hold.BindingKey) && dispatch.DispatchedAt >= probe.Event.OccurredAt);
                return observations.FirstOrDefault(item => item.Task == task && item.Dispatch == dispatch);
            })
            .Where(item => item.Verification is not null).Distinct().ToArray();
        // Applicability is judged at completion; a later exhaustion must not erase prior attempts.
        bool InCurrentGeneration((TaskSpec Task, TaskVerificationRecord Verification, TaskDispatchRecord? Dispatch) item) =>
            IsCurrentDispatch(all, hold.BindingKey, item.Task, item.Dispatch!, item.Verification.CompletedAt) &&
            (reset is null || LatestReset(all, hold.BindingKey, item.Verification.CompletedAt)?.Marker.IntentId == reset.Marker.IntentId);
        var completedProbes = probeResults.Where(InCurrentGeneration)
            .Select(item => item.Verification)
            .Where(verification => verification is not null && verification.CompletedAt >= firstExhaustion &&
                verification.ProviderFailureKind != ProviderFailureKind.BudgetExhausted)
            .Select(verification => verification!).Distinct().ToArray();
        var k = observations.Count(item => item.Verification.ProviderFailureKind == ProviderFailureKind.BudgetExhausted &&
            item.Verification.CompletedAt >= recoveryAt && (reset is null ||
                item.Dispatch!.DispatchedAt >= reset.AppliedAt && item.Dispatch.DispatchedAt >= reset.Marker.ResetAt &&
                InCurrentGeneration(item)));
        var attempt = (reset is null ? Math.Max(1, k) : 1 + k) + completedProbes.Length + standalone.Length;
        DateTimeOffset? resetAt = null;
        var source = "backoff";
        if (reset is not null)
        {
            resetAt = reset.Marker.ResetAt;
            source = "operator";
        }
        else if (TryGetResetAt(exhaustion, out var parsed))
        {
            resetAt = parsed;
            source = "parsed";
        }
        var latestCompletion = completedProbes.Select(verification => verification.CompletedAt)
            .Concat(standalone.Select(item => item.Event.OccurredAt)).Append(exhaustion.CompletedAt).Max();
        var next = completedProbes.Length == 0 && standalone.Length == 0 && (reset is null || k == 0) && resetAt is { } known ? known :
            latestCompletion.AddMinutes(Math.Min(30 * Math.Pow(2, Math.Min(attempt - 1, 4)), 360)).ToUniversalTime();
        var inFlight = latestProbe.Event is not null && dispatches.Any(item => item.Dispatch.DispatchedAt >= latestProbe.Event.OccurredAt &&
            !item.Task.VerificationHistory.Any(verification =>
                SubscriptionLimitEvidence.FindDispatchForVerification(item.Task, verification) == item.Dispatch));
        GoalId? admittedGoal = null;
        TaskId? admittedTask = null;
        if (latestProbe.Event is { } probeEvent && TryParseProbe(probeEvent, out var probeMarker) && probeMarker.Due == next &&
            probeMarker.ResetIntentId == reset?.Marker.IntentId &&
            !dispatches.Any(item => item.Dispatch.DispatchedAt >= probeEvent.OccurredAt) &&
            !latestProbe.Goal.IsTerminal &&
            latestProbe.Goal.Tasks.Any(task => task.Id == probeEvent.TaskId && task.Status == WorkTaskStatus.Assigned && task.AssignedAgentId is not null) &&
            !latestProbe.Goal.Timeline.Skip(latestProbe.Index + 1).Any(evt => evt.TaskId == probeEvent.TaskId &&
                evt.Kind is ProgressKind.TaskRedelegated or ProgressKind.ProviderHoldSubstituted or
                    ProgressKind.ProviderCapabilitySubstituted or ProgressKind.TaskCancelled))
        {
            admittedGoal = latestProbe.Goal.Id;
            admittedTask = probeEvent.TaskId;
        }
        var diagnosticChecks = probeResults.Select(item =>
            (At: item.Verification.CompletedAt, Outcome: item.Verification.ProviderFailureKind switch
            {
                ProviderFailureKind.BudgetExhausted => "exhausted",
                ProviderFailureKind.AuthenticationRequired => "authentication-required",
                _ => "inconclusive"
            })).Concat(standaloneHistory.Select(item =>
            {
                TryParseStandaloneResult(item.Event, out var check);
                return (At: item.Event.OccurredAt, Outcome: check.Outcome == "failed" ? "inconclusive" : check.Outcome);
            })).OrderBy(item => item.At).ToArray();
        var lastOutcome = inFlight ? "running" : diagnosticChecks.LastOrDefault().Outcome ?? "none";
        var actionable = observations.Where(item => item.Verification.ProviderFailureKind is
                ProviderFailureKind.BudgetExhausted or ProviderFailureKind.AuthenticationRequired)
            .Select(item => (At: item.Verification.CompletedAt, Kind: item.Verification.ProviderFailureKind))
            .Concat(standaloneHistory.Select(item =>
            {
                TryParseStandaloneResult(item.Event, out var result);
                return (At: item.Event.OccurredAt, Kind: result.FailureKind);
            }).Where(item => item.Kind != ProviderFailureKind.Unknown)).MaxBy(item => item.At);
        return new(hold, attempt, resetAt, source, next, inFlight, admittedGoal, admittedTask, lastOutcome)
        {
            LastProbeAt = diagnosticChecks.Length > 0 ? diagnosticChecks[^1].At : exhaustion.CompletedAt,
            ResetIntentId = reset?.Marker.IntentId,
            LastActionableFailure = actionable.Kind, LastActionableFailureAt = actionable.At
        };
    }

    private static bool SameBinding(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);
    private static bool IsBinding(TaskDispatchRecord dispatch, string bindingKey) => SameBinding(
        SubscriptionLimitEvidence.BuildProviderBindingKey(dispatch.ProviderName ?? dispatch.WorkerName, dispatch.ClaudeCredentialSourceDirectory).Key, bindingKey);
}
