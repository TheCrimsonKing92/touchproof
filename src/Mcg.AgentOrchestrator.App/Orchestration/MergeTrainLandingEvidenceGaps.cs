using System.Collections.ObjectModel;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Only pre-mutation, member-attributable evidence failures belong in this payload.
internal sealed class MergeTrainLandingEvidenceGaps
{
    public MergeTrainLandingEvidenceGaps(IEnumerable<KeyValuePair<GoalId, string>> diagnostics)
    {
        ByGoal = new ReadOnlyDictionary<GoalId, string>(diagnostics
            .GroupBy(item => item.Key)
            .ToDictionary(group => group.Key, group => string.Join("; ", group.Select(item => item.Value))));
    }

    public IReadOnlyDictionary<GoalId, string> ByGoal { get; }
}
