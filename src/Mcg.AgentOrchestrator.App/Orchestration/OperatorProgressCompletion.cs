using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// A requested human attestation is intent data, never proof that it has been applied.
internal static class OperatorProgressCompletion
{
    internal static void Apply(
        AgentOrchestratorKernel kernel,
        Goal goal,
        TaskSpec task,
        OperatorIntentRecord intent,
        ProgressOperatorIntentPayload progress,
        string? observedCandidate,
        Action<TaskVerificationRecord> recordVerification)
    {
        var request = progress.CompletionVerification
            ?? throw new InvalidOperationException("Operator completion requires a manual verification request.");
        _ = OperatorActorIdentity.Format(intent.Actor, intent.ActorKind);
        if (intent.ActorKind != OperatorActorKind.Human || intent.Channel != "cli" ||
            intent.AuthenticationAssurance != "local-process" ||
            intent.Verb != OperatorIntentVerbs.Progress || intent.GoalId != goal.Id.Value ||
            intent.TaskId != task.Id.Value || progress.Status != WorkTaskStatus.Completed ||
            !request.Passed || !string.Equals(request.Note, progress.Message, StringComparison.Ordinal))
            throw new InvalidOperationException("Operator completion requires matching human CLI progress and manual verification intent.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        if (task.Status == WorkTaskStatus.Completed)
            throw new InvalidOperationException(
                $"Task is already completed. Use verify-manual {goal.Id.Value[..8]} {goal.Tasks.ToList().IndexOf(task) + 1} passed <note> for a new attestation.");

        var verification = ManualVerificationRecorder.Create(
            true, request.Note, request.WorkingDirectory, intent.CreatedAt, preserveNote: true);
        var before = kernel.ExportGoalSnapshot(goal.Id);
        var humanInputs = kernel.ExportSnapshot().HumanInputRequests
            .Where(item => item.GoalId == goal.Id.Value).ToArray();
        try
        {
            kernel.ReportTaskProgress(goal.Id, task.Id, progress.Status, progress.Message,
                observedCandidate, correctionSource: CriteriaCorrectionSource.Operator);
            recordVerification(verification);
        }
        catch
        {
            // Restore the whole owning scope, including correction/timeline and input effects.
            // Callers must reacquire Goal/Task references after replacement.
            kernel.ReplaceGoalStateWithSnapshot(before, humanInputs);
            throw;
        }
    }
}
