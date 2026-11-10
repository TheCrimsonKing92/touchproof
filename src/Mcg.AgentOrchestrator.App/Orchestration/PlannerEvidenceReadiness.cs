using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the ordered Planner readiness decisions; neither path changes authority.
internal sealed class PlannerEvidenceReadiness(
    Func<Goal, PlannerBriefAmendmentRevisionPendingHold?> amendment,
    Func<Goal, PlannerOperatorEvidenceConsistencyHold?> consistency)
{
    internal PlannerEvidenceReadiness(AgentOrchestratorKernel kernel) : this(
        goal => kernel.GetPlannerBriefAmendmentRevisionPending(goal.Id),
        goal => kernel.GetPlannerOperatorEvidenceConsistencyHold(goal.Id)) { }

    internal static PlannerEvidenceReadiness ForTick(
        Func<Goal, PlannerBriefAmendmentRevisionPendingHold?>? amendment,
        Func<AgentOrchestratorKernel?> kernel) =>
        new(amendment ?? (_ => null), goal => kernel()?.GetPlannerOperatorEvidenceConsistencyHold(goal.Id));

    internal ConductorAdvanceOutcome.Held? GetHold(Goal goal, GoalLifecycleState state) =>
        amendment(goal)?.ToOutcome(state) ?? consistency(goal)?.ToOutcome(state);
}
