using Mcg.AgentOrchestrator.App.Application;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliWorkerDispatchPreparation
{
    internal static bool Prepare(IReadOnlyList<string> parts, CliExecutionContext context)
    {
        CliArgumentParser.RequirePartCount(parts, 4, "worker-dispatch <task-number> <worker-name> <command-template>");
        var goal = context.CurrentGoal = OrchestratorEntityResolver.RequireGoal(context.CurrentGoal);
        var task = OrchestratorEntityResolver.GetTaskByDisplayNumber(goal, parts[1]);
        var workerExecutionDirectory = CliGoalWorkerDirectory.Resolve(context);
        if (task.RequiredRole != AgentRole.Researcher || goal.RefinedSpec is not null)
            GoalDispatchOperations.EnsureRefinedForSpecConsumer(context.Kernel, context.Workspace, context.Providers, goal);

        var workerTools = ResolveWorkerTools(context, task);
        var briefSource = context.Kernel.BuildTaskBriefSource(goal.Id, task.Id, workingDirectory: workerExecutionDirectory,
            workerToolsAvailable: workerTools is not null);
        var brief = briefSource.ProjectLegacyMarkedTextV1(emitTypedSourceBoundaries: false);
        var preparation = WorkerCommandTemplate.Prepare(brief, parts[2], parts[3], context.Workspace.PromptDirectory,
            WorkerProfileDispatcher.BuildDispatchVariables(task.RequiredRole, workerExecutionDirectory, null));
        context.Kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(parts[2], preparation.Command,
            workerExecutionDirectory, DateTimeOffset.UtcNow, PromptCharacterCount: preparation.PromptCharacterCount,
            FindingContractVersion: AgentOutputDirectives.GetFindingContractVersion(briefSource, brief),
            WorkerToolsDirectory: workerTools));
        PrerequisiteEvidenceTrimNote.RecordIfTrimmed(context.Kernel, goal.Id, task.Id, brief);
        Console.WriteLine($"Prompt: {preparation.PromptPath}");
        TaskTextView.PrintTask(goal, task);
        return true;
    }

    internal static string? ResolveWorkerTools(CliExecutionContext context, TaskSpec task) =>
        GoalDispatchOperations.CreateWorkerToolsInstaller(OrchestratorHome.ResolveForLaunch(context.Workspace.ExecutionDirectory))
            ?.ResolveForDispatch(context.Kernel, context.CurrentGoal!.Id, task);
}
