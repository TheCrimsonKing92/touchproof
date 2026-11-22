namespace Mcg.AgentOrchestrator.Core;

public sealed record ProviderUsageHoldRow(ProviderBudgetExhaustionHold Hold, ProviderBudgetHoldSchedule? Schedule)
{
    public DateTimeOffset? ProviderResetAt => Schedule is null ? Hold.ProviderResetAt : Schedule.ProviderResetAt;
    public ProviderFailureKind Reason => Schedule?.Reason ?? Hold.Reason;
    internal bool HasAdditionalEvidence => ProviderUsageHoldView.HasAdditionalEvidence(Hold, ProviderResetAt);
    public DateTimeOffset? OverdueSince { get; init; }
    public DateTimeOffset? LastObservedAt { get; init; }
    public string? Diagnosis { get; init; }
}

/// <summary>Only the canonical inputs consumed by the existing binding classifier and scheduler.</summary>
public sealed record ProviderUsageTaskEvidence(string TaskId, AgentRole RequiredRole, WorkTaskStatus Status, string? AssignedAgentId,
    TaskDispatchSnapshot? LastDispatch, IReadOnlyList<TaskDispatchSnapshot> DispatchHistory,
    TaskVerificationSnapshot? LastVerification, IReadOnlyList<TaskVerificationSnapshot> VerificationHistory,
    DateTimeOffset? LatestProviderBudgetRecoveryAt)
{
    public TaskProcessSnapshot? LastProcess { get; init; }
}

public sealed record ProviderUsageGoalEvidence(string GoalId, GoalStatus Status,
    IReadOnlyList<ProviderUsageTaskEvidence> Tasks, IReadOnlyList<ProgressEvent> Timeline);

/// <summary>Read-only operator projection of held dispatched bindings and their probe schedules.</summary>
public static class ProviderUsageHoldView
{
    public static IReadOnlyList<ProviderUsageHoldRow> ReadEvidence(
        IReadOnlyList<ProviderUsageGoalEvidence> evidence, DateTimeOffset now,
        IReadOnlyCollection<Goal>? restoredOwners = null)
    {
        // Sparse, invocation-local policy adapters. Never deserialize historical GoalSnapshot/task
        // graphs or restore a kernel. TaskSpec owns legacy last/history merge and verification rules.
        var goals = evidence.Select(row =>
        {
            if (restoredOwners?.FirstOrDefault(goal => goal.Id.Value == row.GoalId) is { } restored)
                return restored;
            var tasks = row.Tasks.Select(task => TaskSpec.FromSnapshot(new TaskSnapshot(
                task.TaskId, "Provider policy input", task.RequiredRole, task.Status, task.AssignedAgentId,
                null, task.LastVerification, task.VerificationHistory, task.LastDispatch, null,
                DispatchHistory: task.DispatchHistory, LatestProviderBudgetRecoveryAt: task.LatestProviderBudgetRecoveryAt)))
                .ToArray();
            var goal = tasks.Length == 0
                ? Goal.CreateMetadataOnlyTerminal(new TerminalGoalMetadata(new GoalId(row.GoalId),
                    GoalStatus.Completed, "Provider policy input"))
                : new Goal(new GoalId(row.GoalId), "Provider policy input", tasks);
            goal.SetStatus(row.Status);
            foreach (var evt in row.Timeline.OrderBy(evt => evt.OccurredAt)) goal.Append(evt);
            return goal;
        }).ToArray();
        return Read(goals, now);
    }

    public static IReadOnlyList<ProviderUsageHoldRow> Read(IReadOnlyCollection<Goal> goals, DateTimeOffset now) =>
        Read(goals, goals, now);

    internal static IReadOnlyList<ProviderUsageHoldRow> Read(IReadOnlyCollection<Goal> holdScope,
        IReadOnlyCollection<Goal> scheduleScope, DateTimeOffset now)
    {
        var holds = new Dictionary<string, ProviderBudgetExhaustionHold>(StringComparer.OrdinalIgnoreCase);
        var memo = new ProviderBudgetHoldMemo(holdScope);
        foreach (var binding in ProviderHoldBinding.Candidates(holdScope))
            if (memo.TryGetExact(binding, out var hold))
                holds.TryAdd(hold.BindingKey, hold);
        return holds.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair =>
        {
            ProviderBudgetHoldSchedule? schedule;
            try { schedule = ProviderBudgetHoldSchedules.Evaluate(scheduleScope, pair.Value, now); }
            catch (InvalidOperationException) { schedule = null; }
            return new ProviderUsageHoldRow(pair.Value, schedule)
            {
                OverdueSince = schedule is { ProbeInFlight: false, AdmittedTaskId: null } && now >= schedule.NextProbeAt
                    ? schedule.NextProbeAt : null,
                LastObservedAt = schedule?.LastProbeAt ?? pair.Value.PlacedAt,
                Diagnosis = schedule is null ? null : FormatDiagnosis(pair.Value, schedule, now)
            };
        }).ToArray();
    }

    public static string FormatStatusLine(ProviderUsageHoldRow row)
    {
        var source = $"source={row.Hold.SourceGoalId.Value[..8]}/{row.Hold.SourceTaskId.Value[..8]}";
        var evidence = row.HasAdditionalEvidence ? $" | reason={row.Reason}" : "";
        evidence += $" | provider_reset={FormatProviderReset(row.ProviderResetAt)}";
        if (row.OverdueSince is { } due && row.Schedule is { } overdue)
            return $"  {row.Hold.BindingKey} | state=probe-overdue | due={ProviderBudgetHoldSchedules.FormatInstant(due)} | balance=unverified since {ProviderBudgetHoldSchedules.FormatInstant(row.LastObservedAt ?? row.Hold.PlacedAt)} | attempt={overdue.Attempt} | next_probe={ProviderBudgetHoldSchedules.FormatInstant(due)} | reset_source={overdue.ResetSource} | last_probe={overdue.LastProbeOutcome} (historical) | {source}{evidence}";
        return row.Schedule is { } schedule
            ? $"  {row.Hold.BindingKey} | attempt={schedule.Attempt} | next_probe={ProviderBudgetHoldSchedules.FormatInstant(schedule.NextProbeAt)} | reset_source={schedule.ResetSource} | last_probe={schedule.LastProbeOutcome} | {source}{evidence}"
            : $"  {row.Hold.BindingKey} | schedule=unavailable | {source}{evidence}";
    }

    public static string FormatProviderReset(DateTimeOffset? reset) =>
        reset is { } at ? ProviderBudgetHoldSchedules.FormatInstant(at) : "unknown";

    // Provider-wide hard budget failures with no reset use the compact hold metadata.
    internal static bool HasAdditionalEvidence(ProviderBudgetExhaustionHold hold, DateTimeOffset? providerResetAt) =>
        hold.Reason == ProviderFailureKind.AuthenticationRequired || hold.HasUsageLimitDiagnostic ||
        hold.BindingScope == "provider+credential" || hold.ModelName is not null || providerResetAt is not null;

    public static string FormatDiagnosis(ProviderBudgetExhaustionHold hold, ProviderBudgetHoldSchedule schedule, DateTimeOffset now)
    {
        var failure = schedule.LastActionableFailure == ProviderFailureKind.AuthenticationRequired
            ? "authentication-required" : "exhausted";
        var at = schedule.LastActionableFailureAt ?? hold.PlacedAt;
        var age = Math.Max(0, (long)(now - at).TotalSeconds);
        return $"latest check: {schedule.LastProbeOutcome} at {ProviderBudgetHoldSchedules.FormatInstant(schedule.LastProbeAt ?? hold.PlacedAt)}; " +
            $"last confirmed: {failure} at {ProviderBudgetHoldSchedules.FormatInstant(at)} (age {age}s); " +
            (failure == "authentication-required" ? $"recovery: sign in to {hold.ProviderName}, then wait for a successful same-binding availability check; " : "balance remains unverified until a successful availability check; ") +
            $"historical {(hold.Reason == ProviderFailureKind.AuthenticationRequired ? "sign-in failure" : "exhaustion")}: {ProviderBudgetHoldSchedules.FormatInstant(hold.PlacedAt)} ({hold.EvidenceReceipt})" +
            (schedule.Reason == ProviderFailureKind.BudgetExhausted && HasAdditionalEvidence(hold, schedule.ProviderResetAt)
                ? $"; provider reset: {FormatProviderReset(schedule.ProviderResetAt)}" : "");
    }
}
