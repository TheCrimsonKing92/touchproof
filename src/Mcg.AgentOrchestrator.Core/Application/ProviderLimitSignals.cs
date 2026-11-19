using System.Text.RegularExpressions;
using static Mcg.AgentOrchestrator.Core.DispatchFailureClassifier;
using static Mcg.AgentOrchestrator.Core.DirtyDispatchEvidence;
using static Mcg.AgentOrchestrator.Core.SubscriptionLimitEvidence;

namespace Mcg.AgentOrchestrator.Core;

public static class ProviderLimitSignals
{
    private static readonly TimeSpan BareClockRetryStalenessTolerance = TimeSpan.FromHours(1);

    private static readonly Regex TryAgainInPattern = new(
        @"\btry again in\s+(?<value>\d+)\s*(?<unit>ms|milliseconds?|s|sec|secs|seconds?|m|mins?|minutes?|h|hrs?|hours?)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ResetsInPattern = new(
        @"\bresets?\s+in\s*:?\s*(?:(?<hours>\d+)\s*h(?:ours?)?)?\s*(?:(?<minutes>\d+)\s*m(?:in(?:ute)?s?)?)?\s*(?:(?<seconds>\d+)\s*s(?:ec(?:ond)?s?)?)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool RequiresSubscriptionLimitReview(TaskSpec task)
    {
        var failureCount = CountRecoverableSubscriptionLimitFailures(task, includeProviderHolds: false);
        return HasRecoverableSubscriptionLimitHistory(task) &&
            task.VerificationHistory.LastOrDefault() is { } latest && !ProviderHoldRetry.IsHoldInterruption(latest) &&
            failureCount >= RecoverableSubscriptionLimitReviewThreshold &&
            task.SubscriptionLimitReviewedFailureCount < CountRecoverableSubscriptionLimitFailures(task);
    }

    public static bool IsSubscriptionRetryDeferred(TaskSpec task, DateTimeOffset now, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (!TryGetSubscriptionLimitRetryAfter(task, out retryAfter))
        {
            return false;
        }

        return retryAfter > now;
    }

    public static bool TryGetProviderSubscriptionCooldown(
        Goal goal,
        TaskId candidateTaskId,
        string providerName,
        DateTimeOffset now,
        out ProviderSubscriptionCooldown cooldown) =>
        TryGetProviderSubscriptionCooldown(goal, candidateTaskId, providerName, null, now, out cooldown);

    public static bool TryGetProviderSubscriptionCooldown(
        Goal goal,
        TaskId candidateTaskId,
        string providerName,
        string? modelName,
        DateTimeOffset now,
        out ProviderSubscriptionCooldown cooldown) =>
        TryGetProviderSubscriptionCooldown(goal, candidateTaskId, providerName, modelName, now, out cooldown, null);

    internal static bool TryGetProviderSubscriptionCooldown(
        Goal goal, TaskId candidateTaskId, string providerName, string? modelName, DateTimeOffset now,
        out ProviderSubscriptionCooldown cooldown, ProviderHoldBinding? candidateBinding)
    {
        cooldown = default!;
        foreach (var task in goal.Tasks)
        {
            // Provider authority belongs to the failed dispatch, independently of the task's
            // current assignment, latest dispatch or task-local deferral release.
            foreach (var verification in task.VerificationHistory)
                if ((task.Id != candidateTaskId ||
                     task.SubscriptionDeferralReleasedAt is { } releasedAt && verification.CompletedAt <= releasedAt) &&
                    TryGetSubscriptionCooldown(task, verification, now, out var observed) &&
                    observed.ProviderName.Equals(providerName, StringComparison.OrdinalIgnoreCase) &&
                    (candidateBinding is null || !ProviderHoldEvidence.Read(verification).Confirmed ||
                        FindDispatchForVerification(task, verification) is { } dispatch &&
                        ProviderHoldBinding.From(dispatch).Key.Equals(candidateBinding.Key, StringComparison.OrdinalIgnoreCase)) &&
                    observed.AppliesToModel(modelName) &&
                    (cooldown is null || observed.RetryAfter > cooldown.RetryAfter))
                    cooldown = observed;
        }

        return cooldown is not null;
    }

    public static string GetSubscriptionModelName(AgentDefinition agent) =>
        string.IsNullOrWhiteSpace(agent.Subscription?.ModelAlias) ? agent.Model.ModelName : agent.Subscription.ModelAlias;

    public static string? GetSubscriptionDeferralProvider(TaskSpec task)
    {
        var dispatch = task.LastDispatch;
        if (task.SubscriptionRetryAfter is null && task.VerificationHistory.LastOrDefault() is { } verification)
        {
            dispatch = verification.DispatchStartedAt is { } startedAt
                ? task.DispatchHistory.LastOrDefault(record => record.DispatchedAt == startedAt)
                : null;
            dispatch ??= task.DispatchHistory.LastOrDefault(record => record.DispatchedAt <= verification.CompletedAt);
        }
        return dispatch?.ProviderName;
    }

    public static bool TryGetTaskSubscriptionCooldown(
        TaskSpec task, DateTimeOffset now, out ProviderSubscriptionCooldown cooldown)
    {
        cooldown = default!;
        return IsSubscriptionRetryDeferred(task, now, out var retryAfter) &&
            task.VerificationHistory.LastOrDefault() is { } verification &&
            TryGetSubscriptionCooldown(task, verification, now, out cooldown, task.SubscriptionRetryAfter) &&
            cooldown.RetryAfter == retryAfter;
    }

    private static bool TryGetSubscriptionCooldown(TaskSpec task, TaskVerificationRecord verification,
        DateTimeOffset now, out ProviderSubscriptionCooldown cooldown, DateTimeOffset? storedFallback = null)
    {
        cooldown = default!;
        if (verification.Succeeded || !IsRecoverableSubscriptionLimitFailure(verification))
            return false;
        var evidence = ProviderHoldEvidence.Read(verification);
        var retryAfter = evidence.ResetAt.GetValueOrDefault();
        if (evidence.ResetAt is null && !TryGetSubscriptionLimitRetryAfter(verification, out retryAfter))
        {
            // Only the task's latest refusal may use its stored recording-time fallback.
            if (storedFallback is not { } fallback || fallback <= verification.CompletedAt)
                return false;
            retryAfter = fallback;
        }
        if (retryAfter <= now)
            return false;
        var dispatch = FindDispatchForVerification(task, verification);
        if (dispatch?.ProviderName is not { Length: > 0 } providerName ||
            !IsSubscriptionProviderCliDispatch(dispatch) ||
            ((verification.DispatchAttemptAt ?? verification.DispatchStartedAt) is { } attemptAt &&
             dispatch.DispatchedAt != attemptAt))
            return false;
        TryGetRecoverableSubscriptionLimitLine(verification, out var limitLine);
        var modelName = ProviderLimitEvidenceParser.GetCliRefusalModelName(limitLine);
        var scope = new ProviderSubscriptionCooldown(providerName, task.Id, retryAfter) { ModelName = modelName };
        var matched = !string.IsNullOrWhiteSpace(dispatch.ModelName) && scope.AppliesToModel(dispatch.ModelName);
        cooldown = new(providerName, task.Id, retryAfter)
        {
            BindingKey = ProviderHoldBinding.From(dispatch).Key,
            EvidenceReceipt = !string.IsNullOrWhiteSpace(verification.StandardErrorPath)
                ? verification.StandardErrorPath : $"verification:{verification.CompletedAt:O}",
            DispatchStartedAt = dispatch.DispatchedAt,
            ModelName = matched ? modelName : null,
            UnmatchedModelName = matched ? null : modelName,
            LimitEvidence = limitLine
        };
        return true;
    }

    public static bool TryGetProviderBudgetExhaustionHold(
        IEnumerable<Goal> goals,
        string providerName,
        string? credentialBinding,
        out ProviderBudgetExhaustionHold hold) =>
        TryGetExactHold(goals, new(providerName, credentialBinding, null), out hold);

    public static bool TryGetProviderBudgetExhaustionHold(
        IEnumerable<Goal> goals,
        string providerName,
        string? credentialBinding,
        string? modelName,
        out ProviderBudgetExhaustionHold hold) =>
        TryGetExactHold(goals, new(providerName, credentialBinding, null), out hold) ||
        !string.IsNullOrWhiteSpace(modelName) && TryGetExactHold(goals, new(providerName, credentialBinding, modelName), out hold);

    internal static bool TryGetExactHold(
        IEnumerable<Goal> goals,
        ProviderHoldBinding requestedBinding,
        out ProviderBudgetExhaustionHold hold)
    {
        ArgumentNullException.ThrowIfNull(goals);
        var goalList = goals.ToArray();
        var observations = new List<(
            Goal Goal,
            TaskSpec Task,
            TaskVerificationRecord Verification,
            TaskDispatchRecord Dispatch,
            ProviderHoldEvidence Evidence)>();
        ProviderBudgetExhaustionHold? latest = null;

        foreach (var goal in goalList)
        {
            foreach (var task in goal.Tasks)
            {
                foreach (var verification in task.VerificationHistory)
                {
                    var dispatch = ProviderHoldBinding.FindDispatch(task, verification);
                    if (dispatch is null || !ProviderHoldBinding.Matches(dispatch, requestedBinding.Key))
                    {
                        continue;
                    }

                    if (ProviderBudgetHoldSchedules.IsStaleProbeResult(goalList, requestedBinding.Key,
                            task, dispatch, verification.CompletedAt)) continue;
                    var evidence = ProviderHoldEvidence.Read(verification);
                    if (HasAvailabilityEvidence(verification) ||
                        evidence.Confirmed && ProviderHoldBinding.From(dispatch, evidence.ScopedModel(dispatch)).Key
                            .Equals(requestedBinding.Key, StringComparison.OrdinalIgnoreCase))
                        observations.Add((goal, task, verification, dispatch, evidence));
                }
            }
        }

        var latestAvailableDispatch = observations
            .Where(observation =>
                HasAvailabilityEvidence(observation.Verification) &&
                ProviderBudgetHoldSchedules.IsCurrentDispatch(goalList, requestedBinding.Key,
                    observation.Task, observation.Dispatch))
            .Select(observation => (DateTimeOffset?)observation.Dispatch.DispatchedAt)
            .Max();
        var reset = ProviderBudgetHoldSchedules.LatestReset(goalList, requestedBinding.Key);
        var checkedAvailabilityAt = goalList.SelectMany(goal => goal.Timeline.Select(evt => (Goal: goal, Event: evt)))
            .Where(item => ProviderBudgetHoldSchedules.TryParseStandaloneResult(item.Event, out var check) &&
                check.Outcome == "available" && check.BindingKey.Equals(requestedBinding.Key, StringComparison.OrdinalIgnoreCase) &&
                ProviderBudgetHoldSchedules.IsApplicable(check, reset) &&
                observations.Any(observation => observation.Goal == item.Goal && observation.Task.Id == item.Event.TaskId &&
                    observation.Evidence.Confirmed &&
                    observation.Verification.CompletedAt == check.PlacedAt))
            .Select(item => { ProviderBudgetHoldSchedules.TryParseStandaloneResult(item.Event, out var check); return (DateTimeOffset?)check.StartedAt; })
            .Max();

        foreach (var observation in observations)
        {
            var (goal, task, verification, dispatch, evidence) = observation;
            if (!evidence.Confirmed ||
                HasExplicitBudgetRecoveryForBinding(task, requestedBinding.Key, verification.CompletedAt) ||
                checkedAvailabilityAt >= verification.CompletedAt ||
                latestAvailableDispatch >= verification.CompletedAt)
            {
                continue;
            }

            var receipt = !string.IsNullOrWhiteSpace(verification.StandardErrorPath)
                ? verification.StandardErrorPath!
                : $"verification:{verification.CompletedAt:O}";
            var candidate = new ProviderBudgetExhaustionHold(
                dispatch.ProviderName ?? dispatch.WorkerName,
                requestedBinding.Key,
                requestedBinding.Base.Scope,
                goal.Id,
                task.Id,
                verification.CompletedAt,
                receipt)
            {
                Reason = evidence.Kind,
                ModelName = evidence.ScopedModel(dispatch),
                ProviderResetAt = evidence.ResetAt
            };
            if (latest is null ||
                candidate.Reason == ProviderFailureKind.BudgetExhausted && latest.Reason != candidate.Reason ||
                candidate.Reason == latest.Reason && candidate.PlacedAt > latest.PlacedAt)
            {
                latest = candidate;
            }
        }

        hold = latest!;
        return latest is not null;
    }

    // Exit zero alone says nothing about whether the provider completed a valid task response.
    internal static bool HasAvailabilityEvidence(TaskVerificationRecord verification) =>
        verification.Succeeded && verification.CompletionVerdictVerifiedSuccess &&
        verification.ProviderFailureKind == ProviderFailureKind.Unknown;

    private static bool HasExplicitBudgetRecoveryForBinding(
        TaskSpec task,
        string bindingKey,
        DateTimeOffset exhaustedAt)
    {
        if (task.LatestProviderBudgetRecoveryAt is not { } recoveredAt || recoveredAt <= exhaustedAt)
        {
            return false;
        }

        var recoveredDispatch = task.DispatchHistory
            .LastOrDefault(dispatch => dispatch.DispatchedAt <= recoveredAt);
        return recoveredDispatch is not null && ProviderHoldBinding.Matches(recoveredDispatch, bindingKey);
    }

    public static bool TryGetSubscriptionLimitRetryAfter(TaskSpec task, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (task.SubscriptionRetryAfter is { } storedRetryAfter)
        {
            retryAfter = storedRetryAfter;
            return true;
        }

        if (task.VerificationHistory.LastOrDefault() is not { Succeeded: false } latest ||
            !IsRecoverableSubscriptionLimitFailure(latest))
        {
            return false;
        }

        if (task.SubscriptionDeferralReleasedAt is { } releasedAt && latest.CompletedAt <= releasedAt)
        {
            return false;
        }

        // RetryTask is the operator's explicit reset boundary. Keep the historical receipt, but do not
        // let a verification from before that boundary regenerate a deferral after the stored value was cleared.
        if (task.LatestRetryAt is { } latestRetryAt &&
            latest.CompletedAt <= latestRetryAt &&
            task.PendingRetryCause != RetryCause.ProviderInterruption)
        {
            return false;
        }

        return TryGetSubscriptionLimitRetryAfter(latest, out retryAfter);
    }

    public static string DescribeSubscriptionRetrySource(TaskSpec task)
    {
        if (task.SubscriptionRetryAfter is not null)
        {
            return "stored task field SubscriptionRetryAfter";
        }

        if (task.VerificationHistory.LastOrDefault() is { } latest)
        {
            var recordNumber = task.VerificationHistory.Count;
            return $"verification history record {recordNumber} of {recordNumber}, completed {latest.CompletedAt:u}";
        }

        return "retry source unavailable";
    }

    public static bool TryGetSubscriptionLimitRetryAfter(TaskVerificationRecord verification, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (IsDirtyDispatchGuardFailure(verification) ||
            !TryGetRecoverableSubscriptionLimitLine(verification, out var output))
        {
            return false;
        }

        return TryParseSubscriptionRetryAfter(output, verification.CompletedAt, out retryAfter);
    }

    internal static bool TryParseSubscriptionRetryAfter(string output, DateTimeOffset completedAt, out DateTimeOffset retryAfter)
    {
        retryAfter = default;
        if (TryGetRelativeRetryAfter(output, out var retryAfterDelay))
        {
            try { retryAfter = completedAt.Add(retryAfterDelay); }
            catch (ArgumentOutOfRangeException) { return false; }
            return true;
        }

        if (TryGetAbsoluteResetRetryAfter(output, completedAt, out retryAfter))
        {
            return true;
        }

        var marker = "try again at ";
        var markerIndex = output.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var start = markerIndex + marker.Length;
        var remaining = output[start..].TrimStart();
        var token = string.Join(
            " ",
            remaining
                .Split([' ', '\r', '\n', '\t', '.', ','], StringSplitOptions.RemoveEmptyEntries)
                .Take(2));

        if (!TimeOnly.TryParse(token, out var time))
        {
            return false;
        }

        var basis = completedAt;
        retryAfter = new DateTimeOffset(
            basis.Year,
            basis.Month,
            basis.Day,
            time.Hour,
            time.Minute,
            0,
            basis.Offset);

        // Provider notices are emitted before the worker exits. A clock time shortly before completion
        // therefore names an already-expired same-day window, while a substantially earlier time still
        // denotes the next occurrence of that clock time.
        if (retryAfter <= basis && basis - retryAfter > BareClockRetryStalenessTolerance)
        {
            try { retryAfter = retryAfter.AddDays(1); }
            catch (ArgumentOutOfRangeException) { return false; }
        }

        return true;
    }

    internal static bool TryGetProviderBudgetExhaustionLine(TaskVerificationRecord verification, out string line) =>
        TryGetProviderBudgetExhaustionLine(verification, out line, out _);

    internal static bool TryGetProviderBudgetExhaustionLine(
        TaskVerificationRecord verification,
        out string line,
        out string source)
    {
        source = "stderr";
        if (ProviderLimitEvidenceParser.TryGetBudgetExhaustionEvidenceLine(
            DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: false, includeStandardError: true),
            out line))
        {
            return true;
        }

        if (ProviderLimitEvidenceParser.TryGetCliCreditExhaustionLine(
            verification.ExitCode,
            DispatchEvidenceSummaries.EnumerateEvidenceLines(verification, includeStandardOutput: true, includeStandardError: false),
            out line))
        {
            source = "stdout";
            return true;
        }

        return false;
    }

    internal static bool TryGetRelativeRetryAfter(string output, out TimeSpan retryAfter)
    {
        try
        {
            var tryAgainMatch = TryAgainInPattern.Match(output);
            if (tryAgainMatch.Success &&
                int.TryParse(tryAgainMatch.Groups["value"].Value, out var retryValue))
            {
                retryAfter = ToTimeSpan(retryValue, tryAgainMatch.Groups["unit"].Value);
                return retryAfter > TimeSpan.Zero;
            }

            var resetsMatch = ResetsInPattern.Match(output);
            if (resetsMatch.Success)
            {
                var hours = ParseIntGroup(resetsMatch, "hours");
                var minutes = ParseIntGroup(resetsMatch, "minutes");
                var seconds = ParseIntGroup(resetsMatch, "seconds");
                retryAfter = new TimeSpan(hours, minutes, seconds);
                return retryAfter > TimeSpan.Zero;
            }
        }
        catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException)
        {
            // The refusal remains evidence even when its reset cannot be represented.
        }

        retryAfter = default;
        return false;
    }

    private static int ParseIntGroup(Match match, string groupName) =>
        match.Groups[groupName].Success && int.TryParse(match.Groups[groupName].Value, out var value)
            ? value
            : 0;

    private static TimeSpan ToTimeSpan(int value, string unit)
    {
        if (unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase) ||
            unit.StartsWith("millisecond", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMilliseconds(value);
        }

        if (unit.StartsWith("m", StringComparison.OrdinalIgnoreCase) &&
            !unit.StartsWith("ms", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromMinutes(value);
        }

        if (unit.StartsWith("h", StringComparison.OrdinalIgnoreCase))
        {
            return TimeSpan.FromHours(value);
        }

        return TimeSpan.FromSeconds(value);
    }

    private static bool TryGetAbsoluteResetRetryAfter(
        string line, DateTimeOffset completedAt, out DateTimeOffset retryAfter) =>
        TryGetCliAbsoluteResetRetryAfter(line, completedAt, out retryAfter);
}
