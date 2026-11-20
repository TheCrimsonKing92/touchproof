using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliBacklogIntakeExecution
{
    internal static bool Execute(
        IReadOnlyList<string> args,
        ITransactionalOrchestratorStateRepository stateRepository,
        OrchestratorWorkspace workspace,
        ref IReadOnlyList<AgentDefinition> agents,
        IModelProviderRegistry providers,
        ref WorkerProfileCatalog workerProfiles,
        ref Goal? currentGoal,
        BacklogIntakeContinuationHooks? continuationHooks = null)
    {
        var kernel = stateRepository.LoadAsync().GetAwaiter().GetResult();
        var initialGoalSnapshots = kernel.ExportSnapshot().Goals.ToDictionary(snapshot => snapshot.Id, StringComparer.Ordinal);
        currentGoal = CliPersistentStateRunner.ResolveCurrentGoal(kernel, currentGoal?.Id.Value);

        var persistCheckpoint = CliPersistentStateRunner.CreateCommandCheckpoint(stateRepository, workspace, kernel, initialGoalSnapshots);
        void Persist(AgentOrchestratorKernel checkpoint) => persistCheckpoint(checkpoint);

        var correctionFinalizer = args.Contains("--force-reclaim", StringComparer.OrdinalIgnoreCase) &&
            args.Contains("--create-goal", StringComparer.OrdinalIgnoreCase) &&
            stateRepository is IOrchestratorStateOutboxRepository
            ? CliGoalReplacementExecution.CreateBacklogIntakeCorrectionFinalizer(
                stateRepository, workspace, kernel, initialGoalSnapshots)
            : null;
        var shouldSave = CliCommandDispatcher.ExecuteCommand(
            args,
            kernel,
            workspace,
            ref agents,
            providers,
            ref workerProfiles,
            ref currentGoal,
            () => stateRepository.LoadAsync().GetAwaiter().GetResult(),
            Persist,
            finalizeGoalReplacement: correctionFinalizer,
            recordDurableGoalBaseline: snapshot => initialGoalSnapshots[snapshot.Id] = snapshot,
            backlogContinuation: stateRepository is IOrchestratorStateOutboxRepository outbox
                ? new BacklogIntakeContinuationHost(outbox, Hooks: continuationHooks)
                : null);

        if (shouldSave)
        {
            Persist(kernel);
        }

        currentGoal = CliPersistentStateRunner.ResolveCurrentGoal(kernel, currentGoal?.Id.Value);
        return shouldSave;
    }

}
