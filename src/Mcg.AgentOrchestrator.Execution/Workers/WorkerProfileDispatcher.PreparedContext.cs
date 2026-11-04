using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public static partial class WorkerProfileDispatcher
{
    // Restoring inputs is distinct from refreshing an attempt: a crash-recovery reservation
    // remains bound to the same command, assignment, timestamp and retry fingerprint.
    public static void RestorePreparedContextIfMissing(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task, string integrationBranch,
        WorkerSandboxOptions? sandboxOptions = null, string? orchestratorSkillDirectory = null,
        WorkerTargetHome? targetHome = null, CitedPriorEvidenceResolver? citedPriorEvidenceResolver = null,
        Func<Goal, string?, DeferredNoChangeEvidenceContext>? deferredEvidenceResolver = null,
        string? backlogStorePath = null, Func<Goal, string?, IReadOnlyList<string>>? preparationFindingsResolver = null)
    {
        var dispatch = task.LastDispatch ?? throw new InvalidOperationException("Context restoration requires a recorded dispatch.");
        // Untyped reservations have no section hashes to establish completeness.
        // Regenerate their normal prerequisites without replacing the recorded attempt.
        if (dispatch.ContextPackageReceipt is { } receipt && File.Exists(dispatch.PromptPath) && receipt.Sections
            .Where(section => section.MandatoryRelativePath is not null)
            .All(section =>
            {
                var path = Path.Combine(dispatch.WorkingDirectory, section.MandatoryRelativePath!);
                return File.Exists(path) && WorkerContextArtifact.Hash(File.ReadAllBytes(path)) == section.ContentHash;
            })) return;

        PrepareTask(kernel, goal, task, new WorkerProfile(dispatch.WorkerName, dispatch.Command),
            Path.GetDirectoryName(dispatch.PromptPath) ?? throw new InvalidOperationException("Prepared prompt path is unavailable."),
            dispatch.WorkingDirectory, dispatch.DispatchedAt,
            providerName: dispatch.ProviderName, modelName: dispatch.ModelName,
            reasoningEffort: dispatch.ReasoningEffort, taskComplexity: dispatch.TaskComplexity,
            usesComplexModel: dispatch.UsesComplexModel, reasoningEffortReason: dispatch.ReasoningEffortReason,
            allowPendingRecordedDispatchRefresh: true, dispatchLane: dispatch.DispatchLane,
            modelSelectionReason: dispatch.ModelSelectionReason, reviewRetryCap: dispatch.ReviewRetryCap,
            citedPriorEvidenceResolver: citedPriorEvidenceResolver,
            sandboxOptions: sandboxOptions, plannerSampleCount: dispatch.PlannerSampleCount,
            paidRoute: dispatch.PaidRoute, orchestratorSkillDirectory: orchestratorSkillDirectory,
            integrationBranch: integrationBranch, targetHome: targetHome,
            deferredEvidenceResolver: deferredEvidenceResolver, backlogStorePath: backlogStorePath,
            preparationFindingsResolver: preparationFindingsResolver, restorePreparedContext: true);
    }
}
