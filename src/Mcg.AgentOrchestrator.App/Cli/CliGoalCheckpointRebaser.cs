using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

/// <summary>Releases committed dispatch recovery and adopts authoritative stored checkpoint state.</summary>
internal static class CliGoalCheckpointRebaser
{
    internal static bool Apply(AgentOrchestratorKernel checkpoint, GoalSnapshotSaveResult result)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(result);

        if (result.Disposition is GoalSnapshotSaveDisposition.Saved or GoalSnapshotSaveDisposition.Merged)
            checkpoint.ReleaseDispatchStartRecovery(new GoalId(result.GoalId));

        if (result.PersistedSnapshot is null ||
            result.Disposition is not (GoalSnapshotSaveDisposition.Merged or GoalSnapshotSaveDisposition.Skipped))
        {
            return false;
        }

        // A merge/skip means the store, not the submitted tick snapshot, is authoritative. Keeping
        // the old aggregate alive lets the following tick repeat a side effect for a task that the
        // merged row has already advanced (for example, redispatching a manually verified Tester).
        if (result.PersistedHumanInputRequests is null)
        {
            checkpoint.ReplaceGoalWithSnapshot(result.PersistedSnapshot);
        }
        else
        {
            checkpoint.ReplaceGoalStateWithSnapshot(
                result.PersistedSnapshot,
                result.PersistedHumanInputRequests);
        }
        return true;
    }
}
