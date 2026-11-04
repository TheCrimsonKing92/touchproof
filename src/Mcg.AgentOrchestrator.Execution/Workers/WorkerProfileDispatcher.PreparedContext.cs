using Mcg.AgentOrchestrator.Core;
using System.Text.Json;

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
        // Direct CLI records need no generated prompt/context. A missing working directory
        // still requires workspace recovery; preparation must never create it implicitly.
        if (dispatch.ContextPackageReceipt is null && string.IsNullOrWhiteSpace(dispatch.PromptPath) &&
            Directory.Exists(dispatch.WorkingDirectory)) return;
        if (dispatch.ContextPackageReceipt is null && File.Exists(dispatch.PromptPath) &&
            LegacyPreparedInputsExist(goal, task, dispatch.WorkingDirectory)) return;
        if (dispatch.ContextPackageReceipt is { } receipt && File.Exists(dispatch.PromptPath) && receipt.Sections
            .Where(section => section.MandatoryRelativePath is not null)
            .All(section =>
            {
                var path = Path.Combine(dispatch.WorkingDirectory, section.MandatoryRelativePath!);
                return File.Exists(path) && WorkerContextArtifact.Hash(File.ReadAllBytes(path)) == section.ContentHash;
            })) return;

        var promptRoot = Path.GetDirectoryName(dispatch.PromptPath)
            ?? throw new InvalidOperationException("Prepared prompt path is unavailable.");
        if (!Directory.Exists(dispatch.WorkingDirectory))
            throw new InvalidOperationException("Prepared context restoration requires an existing workspace; recover the worktree before start.");
        var parent = Directory.GetParent(GoalWorktreeLayout.NormalizePath(dispatch.WorkingDirectory));
        if (parent?.Name.Equals(GoalWorktreeLayout.DirectoryName, StringComparison.OrdinalIgnoreCase) == true &&
            !GoalWorktreeLayout.IsExclusiveGoalWorktree(dispatch.WorkingDirectory, goal.Id))
            throw new InvalidOperationException("Prepared context restoration requires this goal's resolved worktree.");

        PrepareTask(kernel, goal, task, new WorkerProfile(dispatch.WorkerName, dispatch.Command),
            promptRoot,
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

    private static bool LegacyPreparedInputsExist(Goal goal, TaskSpec task, string workingDirectory)
    {
        var contextDirectory = Path.Combine(workingDirectory, ".orchestrator-context", goal.Id.Value);
        var registryPath = Path.Combine(contextDirectory, "artifact-registry.json");
        if (!File.Exists(Path.Combine(contextDirectory, "current-task.md")) ||
            !File.Exists(Path.Combine(contextDirectory, "objective.md")) || !File.Exists(registryPath)) return false;
        var registry = JsonSerializer.Deserialize<ContextArtifactRegistryDocument>(File.ReadAllText(registryPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("Worker context artifact registry was empty.");
        // Optional artifacts that were absent at preparation are not restoration triggers.
        if (registry.Artifacts.Where(artifact => artifact.Exists)
            .Any(artifact => !File.Exists(Path.Combine(contextDirectory, artifact.Path)))) return false;
        var requiresHandoff = goal.Tasks.TakeWhile(candidate => candidate.Id != task.Id)
            .Any(candidate => candidate.Status == WorkTaskStatus.Completed && candidate.LastVerification is not null);
        return !requiresHandoff || File.Exists(Path.Combine(workingDirectory, ".orchestrator-handoff.md"));
    }
}
