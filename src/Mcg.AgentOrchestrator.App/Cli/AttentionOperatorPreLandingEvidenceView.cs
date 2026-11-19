using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class AttentionOperatorPreLandingEvidenceView
{
    internal static Dictionary<string, object?> WithRows(Dictionary<string, object?> payload,
        IReadOnlyCollection<Goal>? goals)
    {
        var rows = (goals ?? []).SelectMany(Rows).ToArray();
        if (rows.Length > 0)
            payload.Add("operatorPreLandingEvidence", rows);
        return payload;
    }

    private static IEnumerable<object> Rows(Goal goal)
    {
        foreach (var obligation in AcceptanceCriterionEvidence.OperatorPreLandingBlockers(goal, candidateSha: null))
        {
            var candidate = string.IsNullOrWhiteSpace(obligation.ExpectedCandidateSha)
                ? obligation.CandidateSha : obligation.ExpectedCandidateSha;
            if (string.IsNullOrWhiteSpace(candidate) || obligation.HasSatisfiedEvidenceFor(candidate))
                continue;

            yield return new
            {
                goalId = goal.Id.Value,
                obligationId = obligation.Id,
                criterionNumber = obligation.CriterionNumber,
                requiredScope = obligation.RequiredScope,
                candidateSha = candidate,
                command = $"criterion-evidence-record --goal {goal.Id.Value} {obligation.Id} operator " +
                    $"{candidate} <receipt-id> {QuoteArgument(obligation.RequiredScope)} <passed|failed> <detail>"
            };
        }
    }

    private static string QuoteArgument(string value) => value.Any(char.IsWhiteSpace) ? $"\"{value}\"" : value;
}
