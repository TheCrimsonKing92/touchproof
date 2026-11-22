using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class BuildRootReapCadenceGate
{
    private readonly object sync = new();
    private readonly HashSet<GoalId> liveGoals = [];
    private long? lastPassStarted;

    internal GoalId[] SnapshotPreviouslyLiveGoals()
    {
        lock (sync)
            return liveGoals.ToArray();
    }

    internal TerminalGoalSweepOwnedRootResult? RunIfDue(
        TimeProvider clock, TimeSpan interval, bool force,
        IEnumerable<(GoalId Id, bool IsTerminal)>? goals, Func<TerminalGoalSweepOwnedRootResult> reap)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);
        lock (sync)
        {
            var terminalTransition = false;
            if (goals is not null)
            {
                foreach (var (id, isTerminal) in goals.DistinctBy(goal => goal.Id))
                {
                    if (isTerminal)
                        terminalTransition |= liveGoals.Remove(id);
                    else
                        liveGoals.Add(id);
                }
            }

            var now = clock.GetTimestamp();
            if (!force && !terminalTransition && lastPassStarted is { } last &&
                clock.GetElapsedTime(last, now) < interval)
                return null;

            lastPassStarted = now;
            return reap();
        }
    }
}
