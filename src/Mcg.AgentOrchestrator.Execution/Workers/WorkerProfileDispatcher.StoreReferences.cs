using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class WorkerProfileDispatcher
{
    private static (string Directory, string? Pointer) WriteDispatchContextArtifacts(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        string workingDirectory, IReadOnlyList<string>? preflightFindings, string? providerName, string? modelName,
        CitedPriorEvidenceResolver? resolver, DateTimeOffset dispatchedAt, string? orchestratorSkillDirectory, WorkerTargetHome? targetHome = null,
        string? backlogStorePath = null, string? integrationBranch = null)
    {
        var prerequisiteAnswers = kernel.HumanInputRequests
            .Where(request => request.GoalId == goal.Id && request.Kind == HumanWaitKind.PlannerPrerequisiteEvidence
                && request.IsCompleted && !request.WasDismissed && request.SupersededByRequestId is null
                && !request.IsSyntheticParkedHumanWaitCompletion && !string.IsNullOrWhiteSpace(request.Answer))
            .OrderBy(request => request.AnsweredAt)
            .ThenBy(request => request.Id.Value, StringComparer.Ordinal)
            .Select(request => request.Answer!).ToArray();
        // Prerequisite evidence keeps its existing priority within the shared resolver budgets.
        var clarificationAnswers = kernel.HumanInputRequests
            .Where(request => request.GoalId == goal.Id && request.TaskId is not null && request.TaskId == task.Id
                && request.Kind is HumanWaitKind.SpecClarification or HumanWaitKind.PlannerBriefAmendment
                && request.IsCompleted && !request.WasDismissed && request.SupersededByRequestId is null
                && !request.IsSyntheticParkedHumanWaitCompletion
                && !string.IsNullOrWhiteSpace(request.AuthoritativeAnswer?.Text))
            .OrderBy(request => request.AuthoritativeAnswer!.AnsweredAt)
            .ThenBy(request => request.Id.Value, StringComparer.Ordinal)
            .Select(request => request.AuthoritativeAnswer!.Text);
        var linkedBacklog = LinkedBacklogRecordEvidence.Read(backlogStorePath, goal, task.RequiredRole);
        var contextDirectory = WorkerContextArtifacts.Write(goal, task, workingDirectory, preflightFindings,
            resolver?.Resolve(goal, task), providerName, modelName,
            orchestratorStoreRoot: resolver?.OrchestratorDirectory,
            answeredEvidenceTexts: prerequisiteAnswers.Concat(clarificationAnswers).ToArray(), clock: new DispatchContextClock(dispatchedAt),
            orchestratorSkillDirectory: orchestratorSkillDirectory, targetHome: targetHome,
            linkedBacklogRecord: linkedBacklog?.Record, integrationBranch: integrationBranch);
        try
        {
            var path = Path.Combine(contextDirectory, LinkedBacklogRecordEvidence.FileName);
            return (contextDirectory, linkedBacklog is not null && File.Exists(path) &&
                File.ReadAllText(path) == linkedBacklog.Record ? linkedBacklog.Pointer : null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return (contextDirectory, null);
        }
    }

    private sealed class DispatchContextClock(DateTimeOffset utcNow) : IClock
    {
        public DateTimeOffset UtcNow { get; } = utcNow;
    }
}
