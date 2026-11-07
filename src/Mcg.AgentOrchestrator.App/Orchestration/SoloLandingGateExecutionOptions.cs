using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class SoloLandingGateExecutionOptions
{
    internal static AcceptanceRunExecutionOptions Create(Goal goal) => new(
        AttemptMemberGoalIds: [goal.Id],
        MemberTestRemovals: MemberDeclaredTestRemovals.FromMembers([(goal.Id, goal.Objective)]));
}
