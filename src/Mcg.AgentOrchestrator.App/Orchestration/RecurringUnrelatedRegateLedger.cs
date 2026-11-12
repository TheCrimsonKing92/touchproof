using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class RecurringUnrelatedRegateLedger(AcceptanceFailingTestIndex index)
{
    internal const string Kind = "recurring-unrelated-regate";
    internal int Count(GoalId goal, string candidate) => index.Read().Count(row => row.Kind == Kind &&
        row.GoalId == goal.Value && string.Equals(row.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase));

    internal bool Append(GoalId goal, string candidate, DateTimeOffset now)
    {
        var before = Count(goal, candidate);
        index.Append([new(AcceptanceFailingTestIndex.ContractVersion, Kind, goal.Value, now, CandidateSha: candidate)], now);
        // The index is otherwise best effort. A lost bound write must not admit an endless re-gate.
        return Count(goal, candidate) > before;
    }
}
