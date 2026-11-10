using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.Core;

public enum PlannerOperatorEvidenceProblem
{
    MissingOwnership,
    ConflictingOwnership,
    InvalidRequestProvenance
}

public sealed record PlannerOperatorEvidenceDiscrepancy(
    PlannerEvidenceIdentity Source,
    HumanInputRequestId? RequestId,
    int CriterionVersion,
    string Criterion,
    PlannerOperatorEvidenceProblem Problem,
    string Declaration);

// A readiness decision, never an ownership declaration or an evidence receipt.
public sealed record PlannerOperatorEvidenceConsistencyHold(
    GoalId GoalId, int BriefVersion, IReadOnlyList<PlannerOperatorEvidenceDiscrepancy> Discrepancies)
{
    public const string State = "planner-operator-evidence-consistency";
    public string Identity => GoalHoldState.BuildIdentity(State,
        string.Join("\n", Discrepancies.Select(item =>
            $"{item.Source.StableId}:{item.CriterionVersion}:{item.Criterion}:{item.Problem}")));

    public string Render() => $"{State} goal={GoalId.Value} brief={BriefVersion}: " +
        string.Join("; ", Discrepancies.Select(item =>
            $"criterion {item.Source.CriterionIndex} (v{item.CriterionVersion}) '{item.Criterion}' " +
            $"request={item.RequestId?.Value ?? "missing"} source={item.Source.StableId} " +
            $"task={item.Source.RequestingTaskId} key={item.Source.EvidenceKey}: {item.Problem}: {item.Declaration}")) +
        ". Resolve through an authorized Planner brief amendment or an operator criterion-evidence-map decision " +
        "for the current criterion version and candidate. A request or answer does not establish ownership or satisfy evidence.";

    public ConductorAdvanceOutcome.Held ToOutcome(GoalLifecycleState state) =>
        new(state, Render(), Identity) { OperatorEvidenceConsistency = this, Owner = ConductorHoldOwner.Operator };
}
