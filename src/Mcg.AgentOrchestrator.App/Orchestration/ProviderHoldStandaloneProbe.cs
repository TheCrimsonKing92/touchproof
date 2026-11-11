using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>Owned by one conductor. Called synchronously between admission phases, never from a background task.</summary>
internal sealed class ProviderHoldStandaloneProbe(IProviderHoldAvailabilityProbe probe,
    ConductEventLogWriter writer,
    Func<Goal, TaskSpec, IEnumerable<Goal>, WorkerSubscriptionPreflightResult> preflight,
    Func<AgentOrchestratorKernel, ProviderBudgetExhaustionHold, string, DateTimeOffset, bool>? recordRetained = null,
    Func<DateTimeOffset>? utcNow = null, Func<bool>? providerBusy = null, Action<string>? diagnostic = null)
{
    private readonly HashSet<(string Binding, DateTimeOffset Placed, DateTimeOffset Due, int Attempt, string? Reset)> _claims = [];

    internal IReadOnlyList<GoalId> ObserveTick(AgentOrchestratorKernel kernel, DateTimeOffset now)
    {
        var changed = new List<GoalId>();
        try
        {
            if (providerBusy?.Invoke() == true) return changed;
            foreach (var row in ProviderUsageHoldView.Read(kernel.ProviderHoldScope, now))
            {
                if (row.OverdueSince is null || row.Schedule is not { } schedule) continue;
                try { ObserveHold(kernel, row.Hold, schedule, now, changed); }
                catch (Exception error)
                {
                    ReportFault(error, row.Hold.BindingKey);
                    AppendCheck(row.Hold, schedule, "failed", error.GetType().Name + ":" + error.Message, now);
                }
            }
        }
        catch (Exception error) { ReportFault(error, "unavailable"); }
        return changed;
    }

    private void ObserveHold(AgentOrchestratorKernel kernel, ProviderBudgetExhaustionHold hold,
        ProviderBudgetHoldSchedule schedule, DateTimeOffset now, List<GoalId> changed)
    {
        var claim = (hold.BindingKey.ToUpperInvariant(), hold.PlacedAt, schedule.NextProbeAt, schedule.Attempt, schedule.ResetIntentId);
        if (_claims.Contains(claim)) return;
        // Provider-wide, including other credential bindings and processes whose task has moved state.
        if (kernel.ProviderHoldScope.SelectMany(goal => goal.Tasks).Any(task =>
                (task.Status == WorkTaskStatus.Running || task.LastProcess is { IsRunning: true }) &&
                string.Equals(task.LastDispatch?.ProviderName ?? task.LastDispatch?.WorkerName, hold.ProviderName,
                    StringComparison.OrdinalIgnoreCase))) return;
        if (kernel.Goals.Where(goal => !goal.IsTerminal).Any(goal => goal.Tasks.Any(task =>
                task.Status == WorkTaskStatus.Assigned && ProviderBudgetProbeAdmission.WaitsOnBinding(
                    preflight(goal, task, kernel.ProviderHoldScope), hold.BindingKey)))) return;
        var source = kernel.ProviderHoldScope.Single(goal => goal.Id == hold.SourceGoalId);
        var sourceTask = source.Tasks.Single(task => task.Id == hold.SourceTaskId);
        var dispatch = sourceTask.DispatchHistory.Last(record => record.DispatchedAt <= hold.PlacedAt &&
            SubscriptionLimitEvidence.BuildProviderBindingKey(record.ProviderName ?? record.WorkerName,
                record.ClaudeCredentialSourceDirectory).Key.Equals(hold.BindingKey, StringComparison.OrdinalIgnoreCase));
        if (!_claims.Add(claim)) return;
        ProviderHoldProbeResult result;
        try { result = probe.CheckAsync(hold, dispatch, CancellationToken.None).GetAwaiter().GetResult(); }
        catch (Exception error) { result = new("failed", error.GetType().Name + ":" + error.Message); }
        if (result.Outcome is not ("available" or "exhausted" or "failed" or "authentication-required"))
            result = new("failed", "invalid-probe-outcome");
        var completed = utcNow?.Invoke() ?? now;
        if (!ProviderBudgetHoldSchedules.TryGetHoldByBindingKey(kernel.ProviderHoldScope, hold.BindingKey, out var current) ||
            current != hold || ProviderBudgetHoldSchedules.Evaluate(kernel.ProviderHoldScope, current, completed) is not { } latest ||
            latest.NextProbeAt != schedule.NextProbeAt || latest.Attempt != schedule.Attempt || latest.ResetIntentId != schedule.ResetIntentId)
        {
            AppendCheck(hold, schedule, "failed", "stale-probe-result", completed);
            return;
        }
        var note = ProviderBudgetHoldSchedules.FormatStandaloneResult(new(hold.BindingKey, hold.PlacedAt,
            schedule.NextProbeAt, schedule.Attempt, now, result.Outcome, result.Reason) { ResetIntentId = schedule.ResetIntentId });
        var recorded = true;
        try
        {
            if (kernel.Goals.Any(goal => goal.Id == hold.SourceGoalId))
            {
                kernel.RecordTaskNote(hold.SourceGoalId, hold.SourceTaskId, note);
                changed.Add(hold.SourceGoalId);
            }
            else recorded = recordRetained?.Invoke(kernel, hold, note, completed) == true;
        }
        catch (Exception error) { recorded = false; result = new("failed", "recording:" + error.GetType().Name); }
        var diagnosis = recorded && result.Outcome != "available"
            ? ProviderUsageHoldView.FormatDiagnosis(hold, ProviderBudgetHoldSchedules.Evaluate(kernel.ProviderHoldScope, hold, completed), completed)
            : null;
        AppendCheck(hold, schedule, recorded ? result.Outcome : "failed",
            recorded || result.Reason.StartsWith("recording:", StringComparison.Ordinal) ? result.Reason : "source-goal-not-stored", completed, diagnosis);
    }

    private void AppendCheck(ProviderBudgetExhaustionHold hold, ProviderBudgetHoldSchedule schedule,
        string outcome, string reason, DateTimeOffset completed, string? diagnosis = null)
    {
        try
        {
            writer.Append("provider-hold-check", hold.SourceGoalId.Value,
                $"PROVIDER_HOLD_CHECK due={ProviderBudgetHoldSchedules.FormatInstant(schedule.NextProbeAt)} attempt={schedule.Attempt} outcome={outcome} reason={Uri.EscapeDataString(reason)}{(diagnosis is null ? "" : $" diagnosis={Uri.EscapeDataString(diagnosis)}")} binding={hold.BindingKey}", timestamp: completed);
        }
        catch (Exception error) { ReportFault(error, hold.BindingKey); }
    }

    private void ReportFault(Exception error, string binding)
    {
        // The fallback must remain safe even when both the event sink and stderr are broken.
        try
        {
            var message = $"PROVIDER_HOLD_CHECK outcome=failed binding={binding} exception={error.GetType().Name} message={Uri.EscapeDataString(error.Message)}";
            if (diagnostic is { } report) report(message);
            else Console.Error.WriteLine(message);
        }
        catch (Exception) { }
    }

    internal static ProviderHoldStandaloneProbe CreateDefault(OrchestratorWorkspace workspace,
        IReadOnlyList<AgentDefinition> agents, WorkerProfileCatalog profiles, ConductEventLogWriter writer, Func<bool> providerBusy) =>
        new(new ProviderHoldAvailabilityProbe(profiles), writer,
            (goal, task, scope) => WorkerProfileDispatcher.PreflightSubscriptionTask(goal, task, agents, profiles,
                workspace.ExecutionDirectory, DateTimeOffset.UtcNow, providerHoldScope: scope),
            new ProviderResetRetainedSourceRecorder(() => new SqliteOrchestratorStateRepository(workspace.SqliteStatePath)).RecordCheck,
            () => DateTimeOffset.UtcNow, providerBusy);
}
