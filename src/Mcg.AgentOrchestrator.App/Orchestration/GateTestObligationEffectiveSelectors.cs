using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Gate start snapshots the same goal obligations and operator edits used at landing.
internal static class GateTestObligationEffectiveSelectors
{
    internal static GateTestObligation[] Current(Goal goal) => goal.GateTestObligations
        .Where(obligation => obligation.BriefVersion == goal.AuthoritativeBrief.Version).ToArray();

    internal static GateTestObligation Effective(Goal goal, GateTestObligation obligation,
        IEnumerable<HumanInputRequest>? answeredRequests) => answeredRequests is null ? obligation :
        AnsweredSelectorRebinding.ApplySelectorEdits(goal, obligation, answeredRequests);

    internal static IReadOnlyList<string> ForMembers(IEnumerable<Goal> goals, IEnumerable<GoalId> memberIds,
        IEnumerable<HumanInputRequest>? answeredRequests = null)
    {
        try
        {
            var requests = answeredRequests?.ToArray();
            var byId = goals.ToDictionary(goal => goal.Id);
            return memberIds.Select(id => byId[id]).SelectMany(goal => Current(goal)
                    .Where(obligation => GateTestObligationWaivers.FindWaiver(goal, obligation) is null &&
                        GateTestObligationRepositoryKey.IsHome(obligation.RepositoryKey))
                    .SelectMany(obligation => Effective(goal, obligation, requests).Selectors))
                .Distinct(StringComparer.Ordinal).ToArray();
        }
        catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException)
        {
            // An unreadable/missing member cannot silently remove its obligations. This deliberately
            // non-class selector makes selected execution fall back to the full gate before dispatch.
            return [$"Obligation snapshot unavailable: {ex.GetType().Name}: {ex.Message}"];
        }
    }
}
