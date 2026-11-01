using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AwaitingVerificationHoldReasonBuilder
{
    private const int MaxGateDetailLength = 64;

    internal static string Build(Goal goal)
    {
        var worklist = AgentOrchestratorKernel.BuildVerificationWorklist(goal);
        if (worklist.Items.Count == 0)
        {
            return "All tasks done; awaiting task verification gates — auto-reconcile will advance goal to Verified";
        }

        var blocked = worklist.Items
            .Take(3)
            .Select(item =>
                $"task={item.TaskId.Value[..8]} role={item.Role} gate={item.GateStatus} detail={BoundDetail(item.Message)}");
        var remainder = worklist.Items.Count > 3
            ? $"; plus {worklist.Items.Count - 3} more unsatisfied task gate(s)"
            : string.Empty;
        return $"Task verification gates require operator attention: {string.Join("; ", blocked)}{remainder}";
    }

    internal static string? BuildOperatorWait(Goal goal)
    {
        var worklist = AgentOrchestratorKernel.BuildVerificationWorklist(goal);
        if (worklist.Items.Count == 0) return null;

        var blocked = worklist.Items.Select(item =>
        {
            var number = TaskDisplayNumber.Resolve(goal, item.TaskId);
            return $"task {number} role={item.Role} gate={item.GateStatus}: " +
                $"verify-manual {goal.Id.Value[..8]} {number} passed";
        });
        return "AwaitingVerification: task verification gates require operator attention." +
            Environment.NewLine + string.Join(Environment.NewLine, blocked);
    }

    private static string BoundDetail(string detail) =>
        detail.Length <= MaxGateDetailLength
            ? detail
            : $"{detail[..(MaxGateDetailLength - 3)]}...";
}
