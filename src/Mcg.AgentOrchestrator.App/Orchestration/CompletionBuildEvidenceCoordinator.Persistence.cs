using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class CompletionBuildEvidenceCoordinator
{
    private CompletionBuildEvidenceOperation Store(AgentOrchestratorKernel kernel,
        CompletionBuildEvidenceOperation operation, CompletionBuildEvidenceOperation? previous)
    {
        var saved = _persist(operation, previous);
        kernel.RecordCompletionBuildEvidence(new GoalId(saved.GoalId), new TaskId(saved.TaskId), saved);
        return saved;
    }

    private CompletionBuildEvidenceOperation Persist(CompletionBuildEvidenceOperation operation,
        CompletionBuildEvidenceOperation? previous) => new SqliteOrchestratorStateRepository(_statePath)
        .TransactGoalStateAsync(new GoalId(operation.GoalId), (state, _) =>
        {
            var task = state?.Goal.Tasks.SingleOrDefault(t => t.Id == operation.TaskId);
            if (task is null) throw new InvalidOperationException("Completion build evidence lost task authority.");
            var loaded = AgentOrchestratorKernel.FromSnapshot(new OrchestratorSnapshot([state!.Goal], []));
            ValidateOrigin(loaded.GetTask(new GoalId(operation.GoalId), new TaskId(operation.TaskId)), new GoalId(operation.GoalId), operation);
            var current = task.CompletionBuildEvidence;
            if (current is not null && TaskSpec.IsRelaunchRecoveryOf(current, operation) &&
                (task.LastDispatch?.DispatchedAt != operation.DispatchAt || task.Status != WorkTaskStatus.Failed ||
                 task.RequiredRole is not (AgentRole.Developer or AgentRole.Tester)))
                throw new InvalidOperationException("Completion build recovery lost its failed dispatch authority.");
            if (current is null && (previous is not null || task.LastDispatch?.DispatchedAt != operation.DispatchAt || task.Status == WorkTaskStatus.Cancelled))
                throw new InvalidOperationException("Completion build evidence lost attached operation authority.");
            if (current is not null && (previous is null || (!current.HasSameOrigin(operation) && !TaskSpec.IsRelaunchRecoveryOf(current, operation)) ||
                !CompletionBuildEvidenceTarget.From(current).Matches(previous) || current.Phase != previous.Phase ||
                current.Applied != previous.Applied || current.CustodyReleased != previous.CustodyReleased))
                return Task.FromResult((false, state, current));
            if (current is not null && operation.Phase == current.Phase && operation.CustodyReleased == current.CustodyReleased &&
                operation.Applied == current.Applied && CompletionBuildEvidenceTarget.From(current).Matches(operation) && operation.Diagnostic == current.Diagnostic)
                return Task.FromResult((false, state, current));
            loaded.RecordCompletionBuildEvidence(new GoalId(operation.GoalId), new TaskId(operation.TaskId), operation);
            var updated = state! with { Goal = state!.Goal with
            { Tasks = state.Goal.Tasks.Select(t => t.Id == task.Id ? t with { CompletionBuildEvidence = operation } : t).ToArray() } };
            return Task.FromResult((true, (GoalStateSnapshot?)updated, operation));
        }).GetAwaiter().GetResult();

}
