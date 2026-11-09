using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns the qualification decision, separate from the coordinator's durable request/child lifecycle.
internal sealed class CompletionBuildRelaunchRecoveryQualification
{
    internal const string ChannelUnavailable = "worker_build_channel_unavailable";
    internal sealed record Context(Func<IReadOnlyList<DateTimeOffset>> Handoffs, Func<DateTimeOffset> UtcNow,
        Func<GoalId, TaskId, CompletionBuildEvidenceTarget, CompletionBuildEvidenceOperation> Request);
    internal sealed record Result(bool Handled, ConductorAdvanceOutcome.Held? Hold, TaskSpec? ExhaustedTask = null);

    internal static Context? ForWorkspace(AgentOrchestratorKernel? kernel, OrchestratorWorkspace? workspace,
        string? executionDirectory, Func<DateTimeOffset> utcNow)
    {
        if (kernel is null || workspace is null || executionDirectory is null) return null;
        var ledger = new ConductorRelaunchHandoffLedger(workspace.ConductEventsLogPath);
        var coordinator = new CompletionBuildEvidenceCoordinator(workspace.SqliteStatePath, executionDirectory, utcNow);
        return new(ledger.Read, utcNow,
            (goalId, taskId, target) => coordinator.RequestRelaunchRecovery(kernel, goalId, taskId, target));
    }

    internal static bool Qualifies(TaskSpec task, IReadOnlyList<DateTimeOffset> handoffs, DateTimeOffset now) =>
        HasChannelFailure(task) && handoffs.Any(at => at > task.LastDispatch!.DispatchedAt && at <= now);

    private static bool HasChannelFailure(TaskSpec task) =>
        task.Status == WorkTaskStatus.Failed && task.RequiredRole is AgentRole.Developer or AgentRole.Tester &&
        BackgroundDispatchRunner.HasCurrentCompletionBuildWorker(task) &&
        task.CompletionBuildEvidence is
            { Phase: CompletionBuildEvidencePhase.ApparatusFailure, Applied: true, CustodyReleased: true, RecoversOperationId: null } build &&
        (build.Diagnostic.Contains(ChannelUnavailable, StringComparison.Ordinal) ||
         (task.LastVerification?.StandardOutput + "\n" + task.LastVerification?.StandardError).Contains(ChannelUnavailable, StringComparison.Ordinal));

    internal static Result? Evaluate(Goal goal, GoalLifecycleState state, Context? context)
    {
        foreach (var task in goal.Tasks.Where(task => task.Status == WorkTaskStatus.Failed &&
            task.RequiredRole is AgentRole.Developer or AgentRole.Tester && BackgroundDispatchRunner.HasCurrentCompletionBuildWorker(task)))
        {
            var operation = task.CompletionBuildEvidence!;
            if (operation.RecoversOperationId is not null) return Observe(task, operation);
            if (context is null || !HasChannelFailure(task) || !Qualifies(task, context.Handoffs(), context.UtcNow())) continue;
            CompletionBuildEvidenceOperation recovery;
            try { recovery = context.Request(goal.Id, task.Id, CompletionBuildEvidenceTarget.From(operation)); }
            catch (IOException) { continue; }
            catch (InvalidOperationException) { continue; }
            if (!TaskSpec.IsRelaunchRecoveryOf(operation, recovery)) continue;
            if (recovery.Applied) return Observe(task, recovery);
            var reason = $"Completion build relaunch recovery requested: task={task.Id.Value}; " +
                $"original={operation.Id}; operation={recovery.Id}. Existing completion workflow will reconcile this dispatch.";
            return new(true, new(state, reason) { Owner = ConductorHoldOwner.BackgroundAttempt });
        }
        return null;
    }

    // Unapplied includes terminal results awaiting refresh; neither a paid retry nor an operator hold may preempt it.
    private static Result Observe(TaskSpec task, CompletionBuildEvidenceOperation recovery) =>
        !recovery.Applied ? new(true, null) :
        new(false, null, recovery.Phase == CompletionBuildEvidencePhase.Success ? null : task);
}
