using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ParallelAcceptanceAttemptReconciliation
{
    internal static (
        ConductorParallelAcceptanceAttemptDecision[] Running,
        ConductorParallelAcceptanceAttemptDecision? RetainedTerminal) ObserveAndReconcileSiblings(
        ConductorParallelAcceptanceAttemptCoordinator coordinator,
        Goal goal,
        IReadOnlyList<ConductorParallelAcceptanceAttempt> sameGoalAttempts)
    {
        var observed = sameGoalAttempts
            .Select(attempt =>
            {
                var persistedCandidate = ConductorParallelAcceptanceCandidate.Create(
                    goal,
                    attempt.SlotIndex,
                    attempt.ScopePaths ?? [],
                    attempt.BranchHeadSha,
                    attempt.MainHeadSha);
                return coordinator.ObserveExistingAttempt(
                    attempt,
                    persistedCandidate);
            })
            .OrderByDescending(decision => decision.Attempt.StartedAt)
            .ThenByDescending(decision => decision.Attempt.AttemptId, StringComparer.Ordinal)
            .ToArray();
        var running = observed
            .Where(decision => decision.Kind == ConductorParallelAcceptanceAttemptDecisionKind.Running)
            .ToArray();
        var terminal = observed
            .Where(decision => decision.Kind is ConductorParallelAcceptanceAttemptDecisionKind.Completed or
                ConductorParallelAcceptanceAttemptDecisionKind.TerminalWithoutRun)
            .ToArray();

        // Terminal siblings release only their own durable claims. A single newest terminal
        // owns the goal-level transition when every same-goal producer is terminal; otherwise
        // every terminal drains and the remaining live producer keeps the goal held.
        var retainedTerminal = running.Length == 0 ? terminal.FirstOrDefault() : null;
        foreach (var terminalSibling in terminal.Where(decision =>
                     retainedTerminal is null ||
                     !string.Equals(
                         decision.Attempt.AttemptId,
                         retainedTerminal.Attempt.AttemptId,
                         StringComparison.Ordinal)))
        {
            coordinator.MarkReconciled(terminalSibling.Attempt);
        }

        return (running, retainedTerminal);
    }
}
