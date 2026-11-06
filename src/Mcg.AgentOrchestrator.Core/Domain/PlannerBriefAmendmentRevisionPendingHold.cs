using System.Text.Json;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

public sealed record PlannerBriefAmendmentRevisionPendingHold(
    GoalId GoalId, TaskId TaskId, HumanInputRequestId RequestId, int CriterionIndex,
    int RequestedBriefVersion, int CurrentBriefVersion)
{
    public const string State = "amendment-revision-pending";
    public string Identity => $"{State}:{RequestId.Value}:{RequestedBriefVersion}";
    public string Format() => JsonSerializer.Serialize(this);
    public ConductorAdvanceOutcome.Held ToOutcome(GoalLifecycleState state) =>
        new(state, Render(), Identity) { AmendmentRevisionPending = this };
    public string Render() =>
        $"{State} criterion={CriterionIndex} request={RequestId.Value} " +
        $"requestedBriefVersion={RequestedBriefVersion} currentBriefVersion={CurrentBriefVersion}. " +
        "Amendment answered; revise the brief or answer with --amendment decline to replan.";
}
