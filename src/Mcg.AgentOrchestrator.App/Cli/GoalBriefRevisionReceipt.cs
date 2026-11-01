using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class GoalBriefRevisionReceipt
{
    internal static void Write(
        GoalBriefRevisionResult result,
        Goal goal,
        bool resetRefinement,
        OrchestratorStateOutboxRequeueOutcome? refinementResetOutcome)
    {
        WriteSummary(result, resetRefinement);
        if (refinementResetOutcome == OrchestratorStateOutboxRequeueOutcome.ProtectedByLiveLease)
            Console.WriteLine("  refinement: refinement is in progress");
        Console.WriteLine($"  not-yet-started tasks: {FormatRevisionTaskIds(result.NotYetStartedTaskIds)}");
        Console.WriteLine($"  in-flight tasks (continue on prior dispatch snapshot): {FormatRevisionTaskIds(result.InFlightTaskIds)}");
        Console.WriteLine($"  completed tasks (unchanged): {FormatRevisionTaskIds(result.CompletedTaskIds)}");
        if (result.RefinedCriteriaReDerived)
        {
            Console.WriteLine("  refined acceptance criteria:");
            for (var index = 0; index < goal.RefinedSpec!.AcceptanceCriteria.Count; index++)
            {
                Console.WriteLine($"    {index + 1}. {goal.RefinedSpec.AcceptanceCriteria[index].Trim().ReplaceLineEndings(" ")}");
            }
        }
    }

    internal static void WriteSummary(GoalBriefRevisionResult result, bool resetRefinement)
    {
        var refinedCriteriaReceipt = resetRefinement ? "reset" : result.RefinedCriteriaReDerived
            ? result.RefinedCriteriaCount!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "unchanged";
        Console.WriteLine(
            $"Goal brief revised: goal={result.GoalId.Value} authoritative=v{result.AuthoritativeVersion.Version} " +
            $"not-yet-started={result.NotYetStartedTaskIds.Count} in-flight={result.InFlightTaskIds.Count} " +
            $"completed-unchanged={result.CompletedTaskIds.Count} refined-criteria={refinedCriteriaReceipt}");
    }

    private static string FormatRevisionTaskIds(IReadOnlyList<TaskId> taskIds) =>
        taskIds.Count == 0
            ? "none"
            : string.Join(", ", taskIds.Select(taskId => taskId.Value[..Math.Min(8, taskId.Value.Length)]));
}
