using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    internal Func<ConductorParallelAcceptanceAttempt, string, bool>? SupersededAttemptStop { get; init; }
    internal GateCandidateSupersessionStop? CandidateSupersessionStop { get; init; }
    private GateCandidateSupersessionStop? _candidateSupersessionStop;

    private void StopCandidateSupersededAttempts(AgentOrchestratorKernel kernel, ConductorDriver driver,
        List<string> changedGoalLines, HashSet<GoalId> changedGoalIds)
    {
        var stop = CandidateSupersessionStop ?? (_candidateSupersessionStop ??=
            _operatorIntents?.GateCancellation is { } cancellation
                ? new(cancellation.Solo, cancellation.Grouped, cancellation.Events) : null);
        if (stop is null) return;
        try
        {
            var heads = new GateCandidateHeadObservations(
                id => driver.AdmissionFacts.Active ? driver.AdmissionFacts.Reader?.Inputs(id)?.Branch : null,
                id => AdmissionFactsRefReader.TryResolve(driver.ExecutionDirectory,
                    $"refs/heads/{GoalWorktrees.BranchName(id)}", out var sha) ? sha : null);
            var result = stop.Apply(kernel, heads);
            foreach (var line in result.Progress) RecordParallelAcceptanceProgress(line, changedGoalLines);
            changedGoalIds.UnionWith(result.ChangedGoalIds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            RecordParallelAcceptanceProgress($"CANDIDATE_SUPERSESSION_CHECK_FAILED error={SanitizeReason(ex.Message)}", changedGoalLines);
        }
    }

    private void NoteLandingAndStopSupersededAttempts(
        AgentOrchestratorKernel kernel,
        ConductorDriver driver,
        string landedGoalId)
    {
        NoteLandingForTick(landedGoalId);
        string? currentMain = null;
        var mainResolved = false;
        foreach (var goal in kernel.Goals.Where(goal =>
                     goal.Id.Value != landedGoalId && goal.Status != GoalStatus.Completed))
        {
            IReadOnlyList<ConductorParallelAcceptanceAttempt> attempts;
            try
            {
                attempts = driver.ParallelAcceptanceAttemptCoordinator.GetSupersedableGateAttempts(
                    [goal.Id.Value]);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                EmitProgress($"SUPERSEDED_GATE_STOP_FAILED goal={goal.Id.Value} error={SanitizeReason(ex.Message)}");
                continue;
            }

            foreach (var attempt in attempts)
            {
                try
                {
                    if (!mainResolved)
                    {
                        currentMain = driver.ResolveCurrentMainHeadSha(goal);
                        mainResolved = true;
                    }
                    if (attempt.MainHeadSha is null || currentMain is null ||
                        string.Equals(attempt.MainHeadSha, currentMain, StringComparison.Ordinal))
                        continue;

                    var stop = SupersededAttemptStop ??
                        driver.ParallelAcceptanceAttemptCoordinator.RequestSupersededMainStop;
                    stop(attempt, currentMain);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
                {
                    EmitProgress($"SUPERSEDED_GATE_STOP_FAILED goal={goal.Id.Value} attempt={attempt.AttemptId} error={SanitizeReason(ex.Message)}");
                }
            }
        }
        try
        {
            driver.StopInvalidatedFollowerGates();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            EmitProgress($"FOLLOWER_GATE_STOP_FAILED error={SanitizeReason(ex.Message)}");
        }
    }
}
