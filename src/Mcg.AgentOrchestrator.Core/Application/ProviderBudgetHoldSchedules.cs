using System.Globalization;
using System.Text.RegularExpressions;

namespace Mcg.AgentOrchestrator.Core;

using ProviderBindingKey = SubscriptionLimitEvidence.ProviderBindingKey;

public sealed record ProviderBudgetHoldSchedule(
    ProviderBudgetExhaustionHold Hold, int Attempt, DateTimeOffset? ResetAt, string ResetSource,
    DateTimeOffset NextProbeAt, bool ProbeInFlight, GoalId? AdmittedGoalId, TaskId? AdmittedTaskId,
    string LastProbeOutcome)
{
    public DateTimeOffset? LastProbeAt { get; init; }
    public DateTimeOffset? ProviderResetAt { get; init; }
    public ProviderFailureKind Reason => QuotaConfirmedByProbe ? ProviderFailureKind.BudgetExhausted : Hold.Reason;
    internal bool QuotaConfirmedByProbe { get; init; }
    public string? ResetIntentId { get; init; }
    public ProviderFailureKind LastActionableFailure { get; init; }
    public DateTimeOffset? LastActionableFailureAt { get; init; }
}

/// <summary>A schedule is a projection of binding history; admission notes reserve one real task dispatch.</summary>
public static class ProviderBudgetHoldSchedules
{
    public sealed record ProbeMarker(string BindingKey, DateTimeOffset Due, int Attempt, string ResetSource)
    {
        internal ProviderBindingKey? BindingIdentity { get; init; }
        public string? ResetIntentId { get; init; }
        public DateTimeOffset? HoldPlacedAt { get; init; }
    }
    public sealed record ResetMarker(string BindingKey, DateTimeOffset ResetAt, string IntentId)
    {
        internal ProviderBindingKey? BindingIdentity { get; init; }
        public DateTimeOffset? IssuedAt { get; init; }
    }
    public sealed record ResetGeneration(ResetMarker Marker, DateTimeOffset AppliedAt);
    public sealed record StandaloneResult(string BindingKey, DateTimeOffset PlacedAt, DateTimeOffset Due,
        int Attempt, DateTimeOffset StartedAt, string Outcome, string Reason)
    {
        internal ProviderBindingKey? BindingIdentity { get; init; }
        public string? ResetIntentId { get; init; }
        public DateTimeOffset? ProviderResetAt { get; init; }
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

    internal static IEnumerable<(ProviderHoldBindingHistory.Note Note, StandaloneResult Result)> ApplicableWhenRecorded(
        ProviderHoldBindingHistory history,
        IReadOnlyCollection<(ProviderHoldBindingHistory.Note Note, StandaloneResult Result)> results)
    {
        if (results.Count == 0) return [];
        var resetsBefore = history.ResetsBefore(results.Select(item => item.Note));
        return results.Where(item => IsApplicable(item.Result, resetsBefore[item.Note]));
    }

    public static bool IsCurrentDispatch(IEnumerable<Goal> goals, string bindingKey, TaskSpec task,
        TaskDispatchRecord dispatch, DateTimeOffset? asOf = null) =>
        IsCurrentDispatch(ProviderHoldBindingHistory.Read(goals, bindingKey), task, dispatch, asOf);

    internal static bool IsCurrentDispatch(ProviderHoldBindingHistory history, TaskSpec task,
        TaskDispatchRecord dispatch, DateTimeOffset? asOf = null)
    {
        var note = history.DispatchMarker(task, dispatch);
        if (!MatchesGeneration(history, note, dispatch, asOf)) return false;
        return ParseMarker(note)?.HoldPlacedAt is not { } placed ||
            !history.HasCompetingPlacement(task, dispatch, placed, asOf);
    }

    internal static ProbeMarker? ParseMarker(ProviderHoldBindingHistory.Note? note) =>
        note is not null && TryParseProbe(note.Event, out var marker) ? marker : null;

    internal static bool MatchesGeneration(ProviderHoldBindingHistory history, ProviderHoldBindingHistory.Note? note,
        TaskDispatchRecord dispatch, DateTimeOffset? asOf)
    {
        var reset = history.LatestReset(asOf);
        var marker = ParseMarker(note);
        if (reset is null) return marker?.ResetIntentId is null;
        if (dispatch.DispatchedAt < reset.AppliedAt || dispatch.DispatchedAt < reset.Marker.ResetAt) return false;
        return marker is null || marker.ResetIntentId == reset.Marker.IntentId ||
            marker.ResetIntentId is null && note!.Event.OccurredAt > reset.AppliedAt;
    }

    public static bool IsStaleProbeResult(IEnumerable<Goal> goals, string bindingKey, TaskSpec task,
        TaskDispatchRecord dispatch, DateTimeOffset completedAt) =>
        IsStaleProbeResult(ProviderHoldBindingHistory.Read(goals, bindingKey), task, dispatch, completedAt);

    internal static bool IsStaleProbeResult(IEnumerable<Goal> goals, ProviderBindingKey binding, TaskSpec task,
        TaskDispatchRecord dispatch, DateTimeOffset completedAt) =>
        IsStaleProbeResult(ProviderHoldBindingHistory.Read(goals, binding), task, dispatch, completedAt);

    internal static bool IsStaleProbeResult(ProviderHoldBindingHistory history, TaskSpec task,
        TaskDispatchRecord dispatch, DateTimeOffset completedAt) =>
        history.DispatchMarker(task, dispatch) is not null && !IsCurrentDispatch(history, task, dispatch, completedAt);

    internal static bool IsAdmittedProbe(Goal goal, TaskSpec task, TaskDispatchRecord dispatch,
        IReadOnlyCollection<Goal> scope)
    {
        HashSet<int>? legacyProbes = null;
        for (var index = 0; index < goal.Timeline.Count; index++)
        {
            var evt = goal.Timeline[index];
            if (evt.TaskId != task.Id || evt.OccurredAt > dispatch.DispatchedAt ||
                !TryParseProbe(evt, out var marker) || dispatch.DispatchedAt < marker.Due ||
                task.DispatchHistory.Any(prior => prior.DispatchedAt >= evt.OccurredAt && prior.DispatchedAt < dispatch.DispatchedAt) ||
                goal.Timeline.Skip(index + 1)
                    .TakeWhile(later => later.TaskId != task.Id || later.Kind != ProgressKind.TaskDispatchRecorded)
                    .Any(later => later.OccurredAt <= dispatch.DispatchedAt && InvalidatesProbeReservation(later, task.Id)))
                continue;

            if (marker.BindingIdentity is { } identity)
            {
                if (ProviderHoldBinding.Matches(dispatch, identity)) return true;
            }
            else if (ProviderHoldBinding.Matches(dispatch, marker.BindingKey))
                (legacyProbes ??= []).Add(index);
        }

        if (legacyProbes is null) return false;
        var matched = false;
        ProviderHoldBindingHistory.Populate(scope, _ => null, addNote: (_, identity, note) =>
        {
            if (note.Goal.Id == goal.Id && legacyProbes.Contains(note.Index) && identity is not null &&
                ProviderHoldBinding.Matches(dispatch, identity)) matched = true;
        });
        return matched;
    }

    private static bool InvalidatesProbeReservation(ProgressEvent evt, TaskId? taskId) =>
        evt.TaskId == taskId && evt.Kind is ProgressKind.TaskRedelegated or ProgressKind.ProviderHoldSubstituted or
            ProgressKind.ProviderCapabilitySubstituted or ProgressKind.TaskCancelled;

    internal static (string Text, ProviderBindingKey? Identity)? ReadMarkerBinding(ProgressEvent evt) =>
        TryParseProbe(evt, out var probe) ? (probe.BindingKey, probe.BindingIdentity)
        : TryParseReset(evt, out var reset) ? (reset.BindingKey, reset.BindingIdentity)
        : TryParseStandaloneResult(evt, out var result) ? (result.BindingKey, result.BindingIdentity)
        : null;

    public static string FormatStandaloneResult(StandaloneResult result) =>
        $"PROVIDER_BUDGET_CHECK binding={Uri.EscapeDataString(result.BindingKey)} placed_at={FormatInstant(result.PlacedAt)} due={FormatInstant(result.Due)} attempt={result.Attempt} started_at={FormatInstant(result.StartedAt)} outcome={result.Outcome} reason={Uri.EscapeDataString(result.Reason)}" +
        (result.ResetIntentId is { } intent ? $" reset_intent={Uri.EscapeDataString(intent)}" : "") +
        (result.Outcome == "exhausted" && result.ProviderResetAt is { } reset ? $" provider_reset_at={FormatInstant(reset)}" : "") +
        FormatBindingIdentity(result.BindingIdentity);

    public static bool TryParseStandaloneResult(ProgressEvent evt, out StandaloneResult result)
    {
        result = null!;
        if (evt.Kind != ProgressKind.TaskNote || !evt.Message.StartsWith("PROVIDER_BUDGET_CHECK ", StringComparison.Ordinal)) return false;
        var fields = evt.Message.Split(' ');
        if (fields.Length is < 8 or > 11) return false;
        string Value(int index, string key) => fields[index].StartsWith(key + "=", StringComparison.Ordinal)
            ? fields[index][(key.Length + 1)..] : "";
        if (!TryParseInstant(Value(2, "placed_at"), out var placed) || !TryParseInstant(Value(3, "due"), out var due) ||
            !int.TryParse(Value(4, "attempt"), out var attempt) || attempt < 1 ||
            !TryParseInstant(Value(5, "started_at"), out var started) || started < due || started < placed ||
            evt.OccurredAt < started || Value(6, "outcome") is not ("available" or "exhausted" or "failed" or "authentication-required") ||
            Value(1, "binding").Length == 0) return false;
        var next = 8;
        var intent = next < fields.Length ? Value(next, "reset_intent") : "";
        if (intent.Length > 0) next++;
        DateTimeOffset? reset = null;
        if (next < fields.Length && Value(6, "outcome") == "exhausted" &&
            TryParseInstant(Value(next, "provider_reset_at"), out var parsedReset))
        {
            reset = parsedReset;
            next++;
        }
        var binding = Uri.UnescapeDataString(Value(1, "binding"));
        ProviderBindingKey? identity = null;
        if (next < fields.Length && !TryParseBindingIdentity(binding, Value(next++, "binding_identity"), out identity)) return false;
        if (next != fields.Length) return false;
        result = new(binding, placed, due, attempt, started,
            Value(6, "outcome"), Uri.UnescapeDataString(Value(7, "reason")))
            { ResetIntentId = intent.Length > 0 ? Uri.UnescapeDataString(intent) : null, ProviderResetAt = reset, BindingIdentity = identity };
        return true;
    }

    private static readonly Regex ProbePattern = new(
        @"\APROVIDER_BUDGET_PROBE binding=(?<binding>.+) due=(?<at>\S+) attempt=(?<attempt>\d+) reset_source=(?<source>operator|parsed|backoff)(?: reset_intent=(?<intent>\S+))?(?: placed_at=(?<placed>\S+))?(?: binding_identity=(?<identity>\S+))?\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex ResetPattern = new(
        @"\APROVIDER_BUDGET_RESET binding=(?<binding>.+) reset_at=(?<at>\S+) source=operator intent=(?<intent>\S+)(?: binding_identity=(?<identity>\S+))?\z",
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
        (marker.HoldPlacedAt is { } placed ? $" placed_at={FormatInstant(placed)}" : "") +
        FormatBindingIdentity(marker.BindingIdentity);
    public static string FormatReset(ResetMarker marker) =>
        $"PROVIDER_BUDGET_RESET binding={marker.BindingKey} reset_at={FormatInstant(marker.ResetAt)} source=operator intent={marker.IntentId}" +
        FormatBindingIdentity(marker.BindingIdentity);

    private static string FormatBindingIdentity(ProviderBindingKey? identity) =>
        identity is null ? "" : $" binding_identity={identity.Scope}:{Uri.EscapeDataString(identity.Model ?? "")}";

    private static bool TryParseBindingIdentity(string binding, string value, out ProviderBindingKey? identity)
    {
        identity = null;
        var separator = value.IndexOf(':');
        if (separator < 0 || value[..separator] is not ("provider-only" or "provider+credential")) return false;
        var model = value[(separator + 1)..];
        identity = ProviderHoldBinding.Identity(binding, value[..separator], model.Length == 0 ? null : Uri.UnescapeDataString(model));
        return SameBinding(identity.RenderedKey, binding);
    }

    public static bool TryParseProbe(ProgressEvent evt, out ProbeMarker marker)
    {
        marker = null!;
        if (evt.Kind != ProgressKind.TaskNote) return false;
        var match = ProbePattern.Match(evt.Message);
        if (!match.Success || !TryParseInstant(match.Groups["at"].Value, out var at) ||
            !int.TryParse(match.Groups["attempt"].Value, out var attempt) || attempt < 1) return false;
        if (match.Groups["placed"].Success && !TryParseInstant(match.Groups["placed"].Value, out _)) return false;
        var binding = match.Groups["binding"].Value;
        ProviderBindingKey? identity = null;
        if (match.Groups["identity"].Success &&
            !TryParseBindingIdentity(binding, match.Groups["identity"].Value, out identity)) return false;
        marker = new(binding, at, attempt, match.Groups["source"].Value)
        {
            BindingIdentity = identity,
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
        var binding = match.Groups["binding"].Value;
        ProviderBindingKey? identity = null;
        if (match.Groups["identity"].Success &&
            !TryParseBindingIdentity(binding, match.Groups["identity"].Value, out identity)) return false;
        marker = new(binding, at, match.Groups["intent"].Value)
            { IssuedAt = evt.OperatorIntentCreatedAt, BindingIdentity = identity };
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
        var parsed = ProviderHoldEvidence.Read(exhaustion).ResetAt;
        resetAt = parsed.GetValueOrDefault();
        return parsed > exhaustion.CompletedAt;
    }

    internal static bool TryParseProviderReset(string line, DateTimeOffset completedAt, out DateTimeOffset resetAt)
    {
        resetAt = default;
        if (ProviderLimitSignals.TryGetRelativeRetryAfter(line, out var delay))
        {
            try { resetAt = completedAt.Add(delay).ToUniversalTime(); }
            catch (ArgumentOutOfRangeException) { return false; }
            return true;
        }
        var iso = IsoResetPattern.Match(line);
        if (iso.Success && TryParseInstant(iso.Groups["at"].Value, out resetAt))
            return true;
        var match = CalendarResetPattern.Match(line);
        if (!match.Success)
            return SubscriptionLimitEvidence.TryGetCliAbsoluteResetRetryAfter(line, completedAt, out resetAt);
        try
        {
            var zone = TimeZoneInfo.FindSystemTimeZoneById(match.Groups["zone"].Value);
            var year = TimeZoneInfo.ConvertTime(completedAt, zone).Year;
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
            resetAt = candidates.MinBy(at => (at - completedAt).Duration());
            return true;
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
        ProviderBudgetExhaustionHold? found = null;
        var checkedBindings = new HashSet<ProviderBindingKey>();
        foreach (var dispatch in all.SelectMany(goal => goal.Tasks).SelectMany(task => task.DispatchHistory))
        {
            var binding = ProviderHoldBinding.From(dispatch);
            if (!TryCollect(binding) ||
                dispatch.ModelName is { Length: > 0 } model && !TryCollect(binding with { Model = model }))
            {
                hold = null!;
                return false;
            }
        }
        hold = found!;
        return found is not null;

        bool TryCollect(ProviderHoldBinding binding)
        {
            if (!SameBinding(binding.Key, bindingKey) || !checkedBindings.Add(binding.ScopedKey) ||
                !memo.TryGetExact(binding, out var candidate)) return true;
            if (found is not null) return false;
            found = candidate;
            return true;
        }
    }

    public static ProviderBudgetHoldSchedule Evaluate(IEnumerable<Goal> goals, ProviderBudgetExhaustionHold hold, DateTimeOffset now) =>
        Evaluate(ProviderHoldBindingHistory.Read(goals, hold.BindingIdentity), hold, out _);

    internal static ProviderBudgetHoldSchedule Evaluate(ProviderHoldBindingHistory history, ProviderBudgetExhaustionHold hold,
        out IReadOnlyList<ProviderHoldBindingHistory.Observation> currentHolds)
    {
        var dispatches = history.Dispatches;
        var observations = history.ReadObservations().Where(item =>
            !IsStaleProbeResult(history, item.Task, item.Dispatch, item.Verification.CompletedAt)).ToArray();
        var held = observations.Where(item => history.IsHoldObservation(item)).ToArray();
        var exhaustion = held.FirstOrDefault(item => item.Goal.Id == hold.SourceGoalId &&
            item.Task.Id == hold.SourceTaskId && item.Verification.CompletedAt == hold.PlacedAt)
            ?? throw new InvalidOperationException("A provider hold requires a confirmed verification on its binding.");
        var notes = history.Notes;
        var heldPlacements = held.Select(item => (item.Task.Id, item.Verification.CompletedAt)).ToHashSet();
        // Each receipt must have been applicable when observed, on its original source.
        var standaloneResults = new List<(ProviderHoldBindingHistory.Note Note, StandaloneResult Result)>();
        foreach (var item in notes)
            if (item.Event.TaskId is { } taskId && TryParseStandaloneResult(item.Event, out var result) &&
                heldPlacements.Contains((taskId, result.PlacedAt)))
                standaloneResults.Add((item, result));
        var standaloneHistory = ApplicableWhenRecorded(history, standaloneResults)
            .Select(item => item.Note)
            .OrderBy(item => item.Event.OccurredAt).ToArray();
        var latestRecovery = history.Recoveries.Values.Select(item => item.At)
            .Concat(standaloneHistory.Where(item => TryParseStandaloneResult(item.Event, out var result) && result.Outcome == "available")
                .Select(item => item.Event.OccurredAt))
            .Where(at => at <= hold.PlacedAt)
            .DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var available = new ProviderHoldBindingHistory.VerifiedAvailability(observations.Where(item =>
            item.Verification.CompletedAt <= hold.PlacedAt &&
            ProviderLimitSignals.HasAvailabilityEvidence(item.Verification) &&
            IsCurrentDispatch(history, item.Task, item.Dispatch, item.Verification.CompletedAt)));
        currentHolds = held.Where(item => item == exhaustion ||
            item.Verification.CompletedAt >= latestRecovery && !available.Supersedes(item)).ToArray();
        var reset = history.LatestReset();
        var firstExhaustion = currentHolds.Min(item => item.Verification.CompletedAt);
        if (reset?.AppliedAt < firstExhaustion) reset = null;
        standaloneHistory = standaloneHistory.Where(item => TryParseStandaloneResult(item.Event, out var result) &&
            result.PlacedAt >= firstExhaustion).ToArray();
        var standalone = standaloneHistory.Where(item => TryParseStandaloneResult(item.Event, out var result) &&
            IsApplicable(result, reset)).ToArray();
        var probes = notes.Where(item => TryParseProbe(item.Event, out var marker) && SameBinding(marker.BindingKey, hold.BindingKey))
            .OrderBy(item => item.Event.OccurredAt).ToArray();
        var latestProbe = probes.LastOrDefault();
        var byAttempt = observations.ToLookup(item => (item.Task, item.Dispatch));
        var probeResults = probes.Select(probe =>
            {
                return history.FindProbeDispatch(probe) is { } attempt ? byAttempt[attempt].FirstOrDefault() : null;
            })
            .OfType<ProviderHoldBindingHistory.Observation>().Distinct().ToArray();
        // Applicability is judged at completion; a later exhaustion must not erase prior attempts.
        bool InCurrentGeneration(ProviderHoldBindingHistory.Observation item) =>
            IsCurrentDispatch(history, item.Task, item.Dispatch, item.Verification.CompletedAt) &&
            (reset is null || history.LatestReset(item.Verification.CompletedAt)?.Marker.IntentId == reset.Marker.IntentId);
        var scheduleHolds = held.Where(item => hold.Reason != ProviderFailureKind.BudgetExhausted ||
            ProviderHoldEvidence.Read(item.Verification).Kind == ProviderFailureKind.BudgetExhausted).ToHashSet();
        var completedProbes = probeResults.Where(item => InCurrentGeneration(item) && !scheduleHolds.Contains(item))
            .Select(item => item.Verification)
            .Where(verification => verification is not null && verification.CompletedAt >= firstExhaustion)
            .Select(verification => verification!).Distinct().ToArray();
        var k = currentHolds.Count(item => scheduleHolds.Contains(item) && (reset is null ||
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
        var refreshed = standalone.Select(item =>
        {
            TryParseStandaloneResult(item.Event, out var check);
            return check;
        }).LastOrDefault(check => check.Outcome == "exhausted" && check.StartedAt >= hold.PlacedAt);
        var providerReset = refreshed is null ? hold.ProviderResetAt : refreshed.ProviderResetAt;
        if (reset is null && providerReset is { } parsed)
        {
            resetAt = parsed;
            source = "parsed";
        }
        var latestCompletion = completedProbes.Select(verification => verification.CompletedAt)
            .Concat(standalone.Select(item => item.Event.OccurredAt)).Append(hold.PlacedAt).Max();
        var next = completedProbes.Length == 0 && standalone.Length == 0 && (reset is null || k == 0) && resetAt is { } known
            ? (known < hold.PlacedAt ? hold.PlacedAt : known) :
            latestCompletion.AddMinutes(Math.Min(30 * Math.Pow(2, Math.Min(attempt - 1, 4)), 360)).ToUniversalTime();
        if (reset is null && providerReset is { } earliest && earliest > next) next = earliest;
        var inFlight = latestProbe is not null && dispatches.Any(item => item.Dispatch.DispatchedAt >= latestProbe.Event.OccurredAt &&
            !history.ObservationsByAttempt.Contains((item.Task, item.Dispatch)));
        GoalId? admittedGoal = null;
        TaskId? admittedTask = null;
        if (latestProbe?.Event is { } probeEvent && TryParseProbe(probeEvent, out var probeMarker) && probeMarker.Due == next &&
            probeMarker.ResetIntentId == reset?.Marker.IntentId &&
            !dispatches.Any(item => item.Dispatch.DispatchedAt >= probeEvent.OccurredAt) &&
            !latestProbe.Goal.IsTerminal &&
            latestProbe.Goal.Tasks.Any(task => task.Id == probeEvent.TaskId && task.Status == WorkTaskStatus.Assigned && task.AssignedAgentId is not null) &&
            !latestProbe.Goal.Timeline.Skip(latestProbe.Index + 1).Any(evt => InvalidatesProbeReservation(evt, probeEvent.TaskId)))
        {
            admittedGoal = latestProbe.Goal.Id;
            admittedTask = probeEvent.TaskId;
        }
        var diagnosticChecks = probeResults.Select(item =>
            (At: item.Verification.CompletedAt, Outcome: ProviderHoldEvidence.Read(item.Verification).Kind switch
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
        var actionable = held
            .Select(item => (At: item.Verification.CompletedAt, Kind: ProviderHoldEvidence.Read(item.Verification).Kind))
            .Concat(standaloneHistory.Select(item =>
            {
                TryParseStandaloneResult(item.Event, out var result);
                return (At: item.Event.OccurredAt, Kind: result.FailureKind);
            }).Where(item => item.Kind != ProviderFailureKind.Unknown)).MaxBy(item => item.At);
        return new(hold, attempt, resetAt, source, next, inFlight, admittedGoal, admittedTask, lastOutcome)
        {
            LastProbeAt = diagnosticChecks.Length > 0 ? diagnosticChecks[^1].At : hold.PlacedAt,
            ProviderResetAt = providerReset,
            QuotaConfirmedByProbe = standaloneHistory.Any(item =>
                TryParseStandaloneResult(item.Event, out var check) && check.Outcome == "exhausted"),
            ResetIntentId = reset?.Marker.IntentId,
            LastActionableFailure = actionable.Kind, LastActionableFailureAt = actionable.At
        };
    }

    private static bool SameBinding(string left, string right) => left.Equals(right, StringComparison.OrdinalIgnoreCase);

}
