using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    private IGoalLifecycleEventWriter? _holdEscalationEventWriter;

    internal IGoalLifecycleEventWriter? HoldEscalationEventWriter
    {
        get => _holdEscalationEventWriter ?? _cohortEventWriter;
        set => _holdEscalationEventWriter = value;
    }

    private void RecordEscalation(Goal goal, GoalLifecycleState state, string reason) =>
        WriteEscalation(goal, state, reason);

    private LandingEscalationWriteResult WriteEscalation(Goal goal, GoalLifecycleState state, string reason)
    {
        var escalationClock = Stopwatch.StartNew();
        var sinkResult = LandingEscalationWriteResult.Error;
        try
        {
            sinkResult = _writeEscalation(goal, state, reason);
        }
        finally
        {
            escalationClock.Stop();
            EmitGoalPhaseTiming(
                "escalation-write",
                goal,
                escalationClock.Elapsed,
                $"json_ms={sinkResult.JsonElapsedMilliseconds} json={sinkResult.JsonOutcome} " +
                $"collab_ms={sinkResult.CollaborationElapsedMilliseconds} collab={sinkResult.CollaborationOutcome}");
        }
        return sinkResult;
    }

    private ConductorAdvanceResult EscalateFailedGoalRecovery(
        Goal goal, string goalPrefix, ConductorAutonomyPolicy policy, GoalLifecycleState state,
        FailedGoalRecoveryDecision decision, FindingEvidenceBatchClosure? closure, bool terminalFailure)
    {
        var reason = SandboxPreparationStartStatus.EscalationReason(goal, decision.Reason, _recordTaskNote);
        if (closure is not null)
        {
            LandingEscalationWriteResult? delivery = null;
            if (!FindingRoundBatchCoordinator.TryApply(this, goal, closure,
                () => IsFailedGoalRecoveryContextCurrent(goal, policy, state, decision, out _),
                () => (delivery = WriteEscalation(goal, state, reason)).Delivered))
            {
                if (delivery is null)
                    return MakeResult(goal.Id.Value, goalPrefix, policy, FindingRoundBatchCoordinator.ClosedHold(state, decision.Identity));

                var pendingReason = $"Finding batch escalation delivery remains pending (JSON: {delivery.JsonOutcome}, collaboration: {delivery.CollaborationOutcome}).";
                return MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Held(state, pendingReason)
                {
                    Decision = FailedGoalRecoveryDecisionRecords.FindingBatchEscalationDeliveryPending(decision.Identity, pendingReason)
                });
            }
        }
        else RecordEscalation(goal, state, reason);
        return MakeResult(goal.Id.Value, goalPrefix, policy, new ConductorAdvanceOutcome.Escalated(state, reason)
        {
            Decision = FailedGoalRecoveryDecisionRecords.FromPolicy(decision),
            Failure = terminalFailure
                ? ConductorOutcomeText.TerminalFailure(goal, state) : null
        });
    }
}
