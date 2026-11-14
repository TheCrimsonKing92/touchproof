using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SoloLandingGateExecutionOptions
{
    internal static AcceptanceRunExecutionOptions Create(Goal goal, IEnumerable<HumanInputRequest>? answeredRequests = null) => new(
        AttemptMemberGoalIds: [goal.Id],
        GateTestObligationSelectors: GateTestObligationEffectiveSelectors.ForMembers([goal], [goal.Id], answeredRequests),
        MemberTestRemovals: MemberDeclaredTestRemovals.FromMembers([(goal.Id, goal.Objective)]));
}
