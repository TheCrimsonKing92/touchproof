using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// The conductor owns evaluation. The existing handler remains the sole setting writer.
internal sealed class ExecutorSlotController(string hostRoot, string executorsPath,
    ExecutorSettingIntentHandler handler, ConductEventLogWriter events,
    Func<ExecutorSlotControllerSettings>? readSettings = null, Func<string, bool>? isPinned = null,
    Func<DateTimeOffset>? utcNow = null,
    Func<string, DateTimeOffset, DateTimeOffset, IReadOnlyList<ExecutorSlotSample>>? readSamples = null,
    Action<string, ExecutorSlotDecisionRecord>? appendDecision = null,
    Func<ExecutorSlotDecisionRecord, bool>? appendEvent = null)
{
    private readonly object _gate = new();
    internal string LedgerPath => Path.Combine(Path.GetDirectoryName(executorsPath)!, ExecutorSlotDecisionLedger.FileName);

    internal IReadOnlyList<string> Evaluate()
    {
        lock (_gate)
        {
            var lines = new List<string>();
            var now = (utcNow ?? (() => DateTimeOffset.UtcNow))();
            var settings = (readSettings ?? (() => new ExecutorSlotControllerSettings()))();
            settings.Validate();
            var previous = ExecutorSlotDecisionLedger.ReadLatest(LedgerPath);
            foreach (var executor in RemoteLaneExecutorConfiguration.LoadExecutors(executorsPath).Where(e => e.AdmissionEnabled))
            {
                try { lines.Add(EvaluateExecutor(executor, previous.GetValueOrDefault(executor.Id), settings, now)); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException or JsonException)
                { lines.Add($"EXECUTOR_SLOT_CONTROLLER executor={executor.Id} result=failed reason={JsonSerializer.Serialize(error.Message)}"); }
            }
            return lines;
        }
    }

    private string EvaluateExecutor(RemoteLaneExecutorEntry executor, ExecutorSlotDecisionRecord? previous,
        ExecutorSlotControllerSettings settings, DateTimeOffset now)
    {
        var state = ExecutorSlotDecisionLedger.Restore(previous, executor.Slots);
        var samples = readSamples is null ? ExecutorSlotSampleReader.Read(
            RemoteExecutorHealthLedger.ResolveStorePath(hostRoot), executor.Id, now.AddHours(-settings.LookbackHours), now)
            : readSamples(executor.Id, now.AddHours(-settings.LookbackHours), now);
        var decision = ExecutorSlotDecision.Decide(samples, settings, state, executor.Slots,
            isPinned?.Invoke(executor.Id) ?? false, now);
        var transition = !decision.Pinned && decision.ProposedSlots != executor.Slots;
        var claims = new List<IDisposable>();
        try
        {
            var application = decision.Pinned ? "pinned" : "no-change";
            if (transition)
            {
                application = "deferred-busy";
                if (!RemoteExecutorOccupancy.IsOccupied(hostRoot, executor.Id))
                {
                    // Old configuration snapshots can dispatch any of the 16 accepted indices.
                    for (var slot = 0; slot < RemoteLaneExecutorConfiguration.MaximumSlots; slot++)
                    {
                        var claim = RemoteExecutorOccupancy.TryClaimExclusive(hostRoot, executor.Id, slot);
                        if (claim is null) break;
                        claims.Add(claim);
                    }
                    if (claims.Count == RemoteLaneExecutorConfiguration.MaximumSlots &&
                        !RemoteExecutorOccupancy.IsOccupied(hostRoot, executor.Id))
                    {
                        var observed = RemoteLaneExecutorConfiguration.LoadExecutors(executorsPath)
                            .SingleOrDefault(e => e.Id == executor.Id);
                        application = observed is not null && observed.Slots == executor.Slots && observed.AdmissionEnabled
                            ? "pending-handler" : "deferred-setting-changed";
                    }
                }
            }
            var before = state ?? new ExecutorSlotControllerState(executor.Slots, settings);
            // Holds/keep progress without a setting write. Busy/pinned transitions never begin a trial.
            if (!transition && !decision.Pinned) before = decision.NextState;
            var row = new ExecutorSlotDecisionRecord(Guid.NewGuid().ToString("n"), now, executor.Id,
                settings, decision, before, application);
            Exception? ledgerFailure = null;
            try { (appendDecision ?? ExecutorSlotDecisionLedger.Append)(LedgerPath, row); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            { ledgerFailure = error; }
            // Independent attempts: an unavailable ledger must not suppress the required event.
            var eventRow = ledgerFailure is null ? row : row with { AuditStatus = "failed-ledger" };
            var eventWritten = appendEvent?.Invoke(eventRow) ?? events.AppendRequired("executor-slot-decision", null,
                JsonSerializer.Serialize(eventRow, ExecutorSlotDecisionLedger.JsonOptions), now, row.EvaluationId);
            if (ledgerFailure is not null || !eventWritten)
                return $"EXECUTOR_SLOT_CONTROLLER executor={executor.Id} evaluation={row.EvaluationId} result=audit-failed ledger={(ledgerFailure is null ? "written" : "failed")} event={(eventWritten ? "written" : "failed")}";
            if (application == "pending-handler")
            {
                var intent = new OperatorIntentRecord(row.EvaluationId, row.EvaluationId, OperatorIntentVerbs.ExecutorSetSlots,
                    OperatorIntentScopes.Workspace, null,
                    JsonSerializer.Serialize(new ExecutorSetSlotsOperatorIntentPayload(executor.Id, decision.ProposedSlots), OperatorIntentJson.Options),
                    [], "conductor", "conductor-slot-controller", "local-process", now, ActorKind: OperatorActorKind.Agent);
                var change = handler.Apply(intent, now);
                application = change.Changed ? "applied" : "no-change";
            }
            return $"EXECUTOR_SLOT_CONTROLLER executor={executor.Id} evaluation={row.EvaluationId} action={decision.Action} target={decision.ProposedSlots} application={application}";
        }
        finally { foreach (var claim in claims) claim.Dispose(); }
    }
}
