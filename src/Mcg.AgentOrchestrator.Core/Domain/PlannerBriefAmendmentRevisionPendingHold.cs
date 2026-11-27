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

    public string NextCommands()
    {
        var goal = GoalId.Value[..Math.Min(8, GoalId.Value.Length)];
        var request = RequestId.Value[..Math.Min(8, RequestId.Value.Length)];
        var answer = $"attention answer {goal} {request} --text-file <path> --amendment";
        return $"{answer} accept | {answer} decline | revise {goal} --brief-file <path> --reason <reason>";
    }

    public string Render() =>
        $"{State} goal={GoalId.Value} criterion={CriterionIndex} request={RequestId.Value} " +
        $"requestedBriefVersion={RequestedBriefVersion} currentBriefVersion={CurrentBriefVersion}. " +
        $"Amendment answered; next: {NextCommands()}";
}
