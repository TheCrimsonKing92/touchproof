using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the loop-lifetime checker and adapts its tick-scoped observations and progress.
internal sealed class GateCandidateSupersessionTick
{
    private GateCandidateSupersessionStop? _stop;

    internal void Apply(AgentOrchestratorKernel kernel, ConductorDriver driver,
        GateCandidateSupersessionStop? overrideStop, OperatorGateCancellation? cancellation,
        Action<string> recordProgress, HashSet<GoalId> changedGoalIds)
    {
        var stop = overrideStop ?? (_stop ??= cancellation is not null
            ? new(cancellation.Solo, cancellation.Grouped, cancellation.Events) : null);
        if (stop is null) return;
        try
        {
            var heads = new GateCandidateHeadObservations(
                id => driver.AdmissionFacts.Active ? driver.AdmissionFacts.Reader?.Inputs(id)?.Branch : null,
                id => AdmissionFactsRefReader.TryResolve(driver.ExecutionDirectory,
                    $"refs/heads/{GoalWorktrees.BranchName(id)}", out var sha) ? sha : null);
            var result = stop.Apply(kernel, heads);
            foreach (var line in result.Progress) recordProgress(line);
            changedGoalIds.UnionWith(result.ChangedGoalIds);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            recordProgress($"CANDIDATE_SUPERSESSION_CHECK_FAILED error={ConductorDiagnosticText.SanitizeReason(ex.Message)}");
        }
    }
}
