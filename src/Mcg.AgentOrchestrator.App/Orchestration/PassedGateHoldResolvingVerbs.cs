using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PassedGateHoldResolvingVerbs
{
    internal static IReadOnlyList<string> For(PassedGateHoldCategory category, Goal goal, string candidate, string? requestId)
    {
        var id = goal.Id.Value;
        var task = goal.Tasks.Select((item, index) => (item, number: index + 1))
            .LastOrDefault(pair => pair.item.Status is WorkTaskStatus.Assigned or WorkTaskStatus.Failed or WorkTaskStatus.Completed).number;
        var number = task > 0 ? task.ToString(System.Globalization.CultureInfo.InvariantCulture) : "<task#>";
        return category switch
        {
            PassedGateHoldCategory.GateTestObligation =>
                [$"adjudicate --goal {id} {number} reopen-regate --text-file <reason-file> --evidence <ref>",
                 $"retry --goal {id} {number} --cause ContractClarification --text-file <reason-file>",
                 $"goal-amend {id} --waive ... (owner only)"],
            PassedGateHoldCategory.CriterionEvidenceObligation =>
                [$"criterion-evidence-record --goal {id} <obligation-id> operator {candidate} <receipt-id> <scope> <passed|failed> <detail>",
                 $"retry --goal {id} {number} --cause ContractClarification --text-file <reason-file>",
                 $"goal-amend {id} --waive ... (owner only)"],
            PassedGateHoldCategory.LandingDenylist => [$"approve-policy-change {id} {candidate} --text-file <reason-file>"],
            PassedGateHoldCategory.PendingHumanWait => [$"attention answer {id} {requestId}"],
            _ => throw new ArgumentOutOfRangeException(nameof(category))
        };
    }
}
