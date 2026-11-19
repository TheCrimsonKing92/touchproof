using System.Globalization;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ConductorProviderHoldWatch
{
    private sealed record Announced(string SourceGoal, string SourceTask, DateTimeOffset PlacedAt);
    private static readonly ConditionalWeakTable<ConductorBatchLoop, ConductorProviderHoldWatch> Watches = new();
    private readonly ConductEventLogWriter? _writer;
    private readonly Dictionary<string, Announced> _announced = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _restored = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(string Binding, DateTimeOffset Due, int Attempt)> _probes = [];
    private bool _rehydrated;
    private readonly HashSet<(string Binding, DateTimeOffset Due)> _overdue = [];

    internal ConductorProviderHoldWatch(ConductEventLogWriter? writer) => _writer = writer;

    internal static ConductorProviderHoldWatch? For(ConductorBatchLoop loop, ConductEventLogWriter? writer) =>
        writer is null ? null : Watches.GetValue(loop, _ => new ConductorProviderHoldWatch(writer));

    internal static void Attach(ConductorBatchLoop loop, ConductorProviderHoldWatch watch) => Watches.Add(loop, watch);

    internal void ObserveTick(IReadOnlyCollection<Goal> scope, DateTimeOffset now)
    {
        try
        {
            if (_writer is null) return;
            if (!_rehydrated) { Rehydrate(); _rehydrated = true; }
            var held = ProviderUsageHoldView.Read(scope, now).ToDictionary(row => EventBinding(row.Hold.BindingKey), StringComparer.OrdinalIgnoreCase);
            foreach (var binding in _announced.Keys.Concat(held.Keys).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToArray())
            {
                if (!held.TryGetValue(binding, out var row))
                {
                    var old = _announced[binding];
                    var seconds = Math.Max(0, (long)(now - old.PlacedAt).TotalSeconds).ToString(CultureInfo.InvariantCulture);
                    _writer.Append("provider-hold-restored", old.SourceGoal,
                        $"PROVIDER_HOLD_RESTORED source={Prefix(old.SourceGoal)}/{Prefix(old.SourceTask)} placed_at={Instant(old.PlacedAt)} held_seconds={seconds} binding={binding}", timestamp: now);
                    _announced.Remove(binding);
                    _restored[binding] = old.PlacedAt;
                    continue;
                }

                var hold = row.Hold;
                if (!_announced.TryGetValue(binding, out var announced) || announced.PlacedAt != hold.PlacedAt)
                {
                    var schedule = row.Schedule;
                    var reset = row.Reason == ProviderFailureKind.BudgetExhausted
                        ? $" provider_reset={ProviderUsageHoldView.FormatProviderReset(row.ProviderResetAt)}" : "";
                    _restored.Remove(binding);
                    _writer.Append("provider-hold-placed", hold.SourceGoalId.Value,
                        $"PROVIDER_HOLD_PLACED source={Prefix(hold.SourceGoalId.Value)}/{Prefix(hold.SourceTaskId.Value)} placed_at={Instant(hold.PlacedAt)} next_probe={(schedule is null ? "unavailable" : Instant(schedule.NextProbeAt))} reset_source={schedule?.ResetSource ?? "unavailable"} attempt={(schedule?.Attempt ?? 0).ToString(CultureInfo.InvariantCulture)}{(row.OverdueSince is { } overdue ? $" state=probe-overdue due={Instant(overdue)}" : "")}{ScopeField(hold.BindingKey)}{DiagnosisField(row)}{reset} binding={binding}", timestamp: now);
                    _announced[binding] = new(hold.SourceGoalId.Value, hold.SourceTaskId.Value, hold.PlacedAt);
                }
                AnnounceProbe(scope, hold.BindingKey, now);
                if (row.OverdueSince is { } due && _overdue.Add((binding.ToUpperInvariant(), due)))
                    _writer.Append("provider-hold-overdue", hold.SourceGoalId.Value,
                        $"PROVIDER_HOLD_OVERDUE due={Instant(due)} balance=unverified{DiagnosisField(row)} binding={binding}", timestamp: now);
            }
            AnnounceRedispatches(scope, now);
        }
        catch (Exception error)
        {
            // A broken diagnostic sink must not make an observational watch fail the loop either.
            try { Console.Error.WriteLine($"PROVIDER_HOLD_WATCH_FAILED exception={error.GetType().Name} message={error.Message.ReplaceLineEndings("_").Replace(' ', '_').Replace('\t', '_')}"); }
            catch (Exception) { }
        }
    }

    private void AnnounceRedispatches(IReadOnlyCollection<Goal> scope, DateTimeOffset now)
    {
        foreach (var (binding, placed) in _restored.ToArray())
        {
            var next = scope.SelectMany(goal => goal.Tasks.SelectMany(task => task.DispatchHistory
                    .Select(dispatch => (Goal: goal, Task: task, Dispatch: dispatch))))
                .Where(item => item.Dispatch.DispatchedAt > placed &&
                    Matches(item.Dispatch, binding) &&
                    (item.Task.DispatchHistory.Any(prior => prior.DispatchedAt <= placed && Matches(prior, binding)) ||
                     item.Goal.Timeline.Any(evt => evt.TaskId == item.Task.Id && evt.OccurredAt <= item.Dispatch.DispatchedAt &&
                         ProviderBudgetHoldSchedules.TryParseProbe(evt, out var probe) &&
                         EventBinding(probe.BindingKey) == binding && (probe.HoldPlacedAt ?? probe.Due) >= placed)) &&
                    IsRecoveryDispatch(scope, item.Goal, item.Task, item.Dispatch, binding))
                .MinBy(item => item.Dispatch.DispatchedAt);
            if (next.Dispatch is null) continue;
            _writer!.Append("provider-hold-redispatched", next.Goal.Id.Value,
                $"PROVIDER_HOLD_REDISPATCHED task={next.Task.Id.Value} dispatched_at={Instant(next.Dispatch.DispatchedAt)} binding={binding}", timestamp: now);
            _restored.Remove(binding);
        }
    }

    private static bool IsRecoveryDispatch(IReadOnlyCollection<Goal> scope, Goal goal, TaskSpec task,
        TaskDispatchRecord dispatch, string eventBinding)
    {
        if (!ProviderBudgetHoldSchedules.IsAdmittedProbe(goal, task, dispatch)) return true;

        // A task probe counts as resumed work only when its own result established availability.
        var binding = ProviderHoldBinding.From(dispatch).Key;
        if (EventBinding(binding) != eventBinding) binding = ProviderHoldBinding.From(dispatch, dispatch.ModelName).Key;
        return task.VerificationHistory.Any(verification => ProviderLimitSignals.HasAvailabilityEvidence(verification) &&
                   ProviderHoldBinding.FindDispatch(task, verification) == dispatch) &&
               ProviderBudgetHoldSchedules.IsCurrentDispatch(scope, binding, task, dispatch);
    }

    private static bool Matches(TaskDispatchRecord dispatch, string eventBinding) =>
        EventBinding(ProviderHoldBinding.From(dispatch).Key) == eventBinding ||
        EventBinding(ProviderHoldBinding.From(dispatch, dispatch.ModelName).Key) == eventBinding;

    private void AnnounceProbe(IReadOnlyCollection<Goal> scope, string binding, DateTimeOffset now)
    {
        var redacted = EventBinding(binding);
        foreach (var (goal, note) in scope.SelectMany(goal => goal.Timeline.Select(note => (goal, note)))
                     .OrderByDescending(pair => pair.note.OccurredAt))
        {
            if (!ProviderBudgetHoldSchedules.TryParseProbe(note, out var probe) || note.TaskId is not { } task ||
                !string.Equals(probe.BindingKey, binding, StringComparison.OrdinalIgnoreCase)) continue;
            var key = (redacted.ToUpperInvariant(), probe.Due, probe.Attempt);
            if (!_probes.Contains(key))
            {
                _writer!.Append("provider-hold-probe", goal.Id.Value,
                    $"PROVIDER_HOLD_PROBE probe={Prefix(goal.Id.Value)}/{Prefix(task.Value)} due={Instant(probe.Due)} attempt={probe.Attempt.ToString(CultureInfo.InvariantCulture)} binding={redacted}", timestamp: now);
                _probes.Add(key);
            }
            return;
        }
    }

    private void Rehydrate()
    {
        if (!File.Exists(_writer!.CurrentPath)) return;
        using var stream = new FileStream(_writer.CurrentPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
        {
            if (!ConductEventFileSource.TryParse(line, out var evt) || evt is null ||
                OwnerProviderHoldNarration.Binding(evt) is not { Length: > 0 } binding) continue;
            binding = EventBinding(binding);
            if (evt.EventKind == "provider-hold-redispatched") _restored.Remove(binding);
            else if (evt.EventKind == "provider-hold-restored")
            {
                _announced.Remove(binding);
                if (TryInstant(OwnerEventFields.Field(evt, "placed_at"), out var restoredPlaced))
                    _restored[binding] = restoredPlaced;
            }
            else if (evt.EventKind == "provider-hold-overdue" && TryInstant(OwnerEventFields.Field(evt, "due"), out var overdue))
                _overdue.Add((binding.ToUpperInvariant(), overdue));
            else if (evt.EventKind == "provider-hold-placed" &&
                     TryInstant(OwnerEventFields.Field(evt, "placed_at"), out var placed) &&
                     OwnerEventFields.Field(evt, "source")?.Split('/') is { Length: 2 } source &&
                     source.All(value => !string.IsNullOrWhiteSpace(value)) && !string.IsNullOrWhiteSpace(evt.GoalId))
            {
                _restored.Remove(binding);
                _announced[binding] = new(evt.GoalId, source[1], placed);
            }
            else if (evt.EventKind == "provider-hold-probe" &&
                     TryInstant(OwnerEventFields.Field(evt, "due"), out var due) &&
                     int.TryParse(OwnerEventFields.Field(evt, "attempt"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var attempt) && attempt > 0)
                _probes.Add((binding.ToUpperInvariant(), due, attempt));
        }
    }

    private static bool TryInstant(string? text, out DateTimeOffset instant) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out instant);
    private static string DiagnosisField(ProviderUsageHoldRow row) =>
        row.Schedule is { LastProbeOutcome: "none", ResetIntentId: null } && row.OverdueSince is null &&
        !row.HasAdditionalEvidence &&
        EventBinding(row.Hold.BindingKey) == row.Hold.BindingKey ? "" : row.Diagnosis is { } diagnosis
        ? $" diagnosis={Uri.EscapeDataString(diagnosis)}" : "";
    private static string ScopeField(string binding) => EventBinding(binding) == binding ? "" :
        $" scope={Uri.EscapeDataString(OwnerProviderHoldNarration.DisplayName(binding))}";
    internal static string EventBinding(string binding)
    {
        var separator = binding.IndexOf("::", StringComparison.Ordinal);
        if (separator < 0 || binding[(separator + 2)..] == "<provider-default>" ||
            binding[(separator + 2)..].StartsWith("scope-", StringComparison.Ordinal)) return binding;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(binding.ToUpperInvariant())));
        return binding[..(separator + 2)] + "scope-" + digest[..16].ToLowerInvariant();
    }
    private static string Instant(DateTimeOffset instant) => ProviderBudgetHoldSchedules.FormatInstant(instant);
    private static string Prefix(string id) => id[..Math.Min(8, id.Length)];
}
