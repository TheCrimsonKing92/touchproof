using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class GateObligationSelectorDeveloperRouting
{
    internal static void Route(AgentOrchestratorKernel kernel, GoalId goalId, string obligationId,
        string selector, string candidateRevision, GateObligationSelectorPreAdmissionClassifier.Classification classification,
        string candidateClasses, Action<string> emit)
    {
        var tuplePrefix = $"GATE_OBLIGATION_SELECTOR_UNRESOLVED obligation={obligationId} selector={selector} candidate={candidateRevision} ";
        var eventContext = $"goal={goalId.Value} obligation={obligationId} selector={selector} candidate={candidateRevision}";
        string progress;
        try
        {
            var goal = kernel.GetGoal(goalId);
            lock (goal)
            {
                var developers = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer);
                // Feedback belongs to this goal. Check every Developer task in case the target changed.
                if (developers.SelectMany(task => task.CriterionRetryFeedback)
                    .Any(line => line.StartsWith(tuplePrefix, StringComparison.Ordinal))) return;

                var target = developers.LastOrDefault(task => task.Status == WorkTaskStatus.Completed) ??
                    developers.LastOrDefault();
                if (target is null)
                {
                    progress = $"GATE_TEST_OBLIGATION_PRE_ADMISSION_ROUTE_SKIPPED {eventContext} reason=no-developer-task";
                }
                else
                {
                    var feedback = $"{tuplePrefix}kind={classification.Kind}. Effective selector: {classification.Selector}. " +
                        $"Candidate classes considered: {candidateClasses}. " +
                        "Update the obligation to name one declared test class or add the intended test class.";
                    var retry = kernel.RecordCriterionRetryFeedback(goalId, target.Id, [..target.CriterionRetryFeedback, feedback]);
                    progress = $"GATE_TEST_OBLIGATION_PRE_ADMISSION_ROUTED {eventContext} task={target.Id.Value} retry={retry}";
                }
            }
        }
        catch (Exception ex)
        {
            progress = $"GATE_TEST_OBLIGATION_PRE_ADMISSION_ROUTE_FAILED {eventContext} {ex.GetType().Name}: {ex.Message}";
        }
        emit(progress);
    }
}
