using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

// Target reloads refresh their baselines; ordinary loads preserve baselines for held changes.
internal static class ConductLoopTargetLoader
{
    internal static AgentOrchestratorKernel LoadOperatorIntentTargets(
        IOrchestratorStateQueries repository, IReadOnlyCollection<GoalId> goalIds,
        IDictionary<string, GoalSnapshot> tickBaselines)
    {
        var loaded = repository.LoadGoalsAsync(goalIds).GetAwaiter().GetResult();
        foreach (var snapshot in loaded.ExportSnapshot().Goals)
            tickBaselines[snapshot.Id] = snapshot;
        return loaded;
    }

    internal static void CaptureMissingTickBaselines(
        IDictionary<string, GoalSnapshot> tickBaselines,
        AgentOrchestratorKernel loadedKernel)
    {
        ArgumentNullException.ThrowIfNull(tickBaselines);
        ArgumentNullException.ThrowIfNull(loadedKernel);
        foreach (var goal in loadedKernel.ExportSnapshot().Goals)
            tickBaselines.TryAdd(goal.Id, goal);
    }

    internal static AgentOrchestratorKernel LoadKernelForTick(
        IOrchestratorStateQueries stateRepository,
        IOperatorIntentStore operatorIntentStore,
        IReadOnlyCollection<string> trackedGoalIds,
        string executionDirectory,
        GoalReloadMemory reloadMemory,
        IDictionary<string, GoalSnapshot> tickBaselines)
    {
        var actionableGoalIds = ConductorTickStepLedger.MeasureKernelReload(ConductorTickStepLedger.ActionableGoalIdQuery,
            () => operatorIntentStore.ListActionableGoalIdsAsync().GetAwaiter().GetResult());
        var loaded = LoadKernel(
            stateRepository,
            actionableGoalIds.Concat(trackedGoalIds).Distinct(StringComparer.Ordinal).ToArray(),
            executionDirectory, reloadMemory);
        ConductorTickStepLedger.MeasureKernelReload(ConductorTickStepLedger.TickBaselineCapture,
            () => ConductLoopTargetLoader.CaptureMissingTickBaselines(tickBaselines, loaded));
        return loaded;
    }

    internal static AgentOrchestratorKernel LoadKernel(
        IOrchestratorStateQueries stateRepository,
        IReadOnlyCollection<string>? additionalHydratedGoalIds = null,
        string? executionDirectory = null, GoalReloadMemory? reloadMemory = null)
    {
        var requestedGoalIds = additionalHydratedGoalIds ?? [];
        var summaries = ConductorTickStepLedger.CountMetadataRows(
            ConductorTickStepLedger.MeasureKernelReload(ConductorTickStepLedger.GoalMetadataList,
                () => stateRepository.ListLiveConductLoopGoalMetadataAsync(requestedGoalIds).GetAwaiter().GetResult()));
        var hydratedIds = summaries
            .Where(summary =>
                !ConductLoopGoalStatus.IsTerminal(summary.Status) &&
                !summary.Status.Equals(GoalStatus.Parked.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(summary => new GoalId(summary.Id))
            .Concat(requestedGoalIds.Select(id => new GoalId(id)))
            .Distinct()
            .ToArray();
        var kernel = ConductorTickStepLedger.CountGoalReload(
            GoalReloadMemory.LoadAsync(stateRepository, hydratedIds, reloadMemory,
                ConductorTickStepLedger.KernelReloadTimingSink()).GetAwaiter().GetResult());
        var dependencyMetadata = ConductorTickStepLedger.MeasureKernelReload(
            ConductorTickStepLedger.TerminalDependencyMetadataRead,
            () => LoadConductLoopDependencyMetadata(stateRepository, kernel, executionDirectory));
        return ConductorTickStepLedger.MeasureKernelReload(ConductorTickStepLedger.KernelMerge,
            () => MergeConductLoopDependencyStatuses(kernel, summaries, dependencyMetadata));
    }

    private static ConductLoopDependencyMetadata[] LoadConductLoopDependencyMetadata(
        IOrchestratorStateQueries stateRepository,
        AgentOrchestratorKernel kernel,
        string? executionDirectory)
    {
        var hydratedNonTerminalIds = kernel.Goals
            .Where(goal => !ConductLoopGoalStatus.IsTerminal(goal.Status.ToString()))
            .Select(goal => goal.Id)
            .ToHashSet();
        var dependencyIds = kernel.Goals
            .SelectMany(goal => goal.DependsOn)
            .Where(id => !hydratedNonTerminalIds.Contains(id))
            .Select(id => id.Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (dependencyIds.Length == 0)
            return [];

        var statuses = stateRepository.ListConductLoopGoalStatusesByIdAsync(dependencyIds).GetAwaiter().GetResult();
        return statuses.Select(goal => ConductLoopGoalStatus.IsTerminal(goal.Value)
                ? ReadConductLoopDependencyMetadata(goal.Key, goal.Value, executionDirectory)
                : new ConductLoopDependencyMetadata(goal.Key, goal.Value, IsLanded: false))
            .ToArray();
    }

    private static AgentOrchestratorKernel MergeConductLoopDependencyStatuses(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<GoalSummary> summaries,
        IReadOnlyList<ConductLoopDependencyMetadata> dependencyMetadata)
    {
        kernel.MarkKnownDependencyGoalStatuses(summaries.Select(summary =>
            new KeyValuePair<GoalId, string>(new GoalId(summary.Id), summary.Status)));
        kernel.MarkKnownDependencyGoalStatuses(dependencyMetadata.Select(metadata =>
            new KeyValuePair<GoalId, string>(new GoalId(metadata.GoalId), metadata.Status)));
        kernel.MarkKnownCompletedDependencyGoals(dependencyMetadata
            .Where(metadata => metadata.IsLanded)
            .Select(metadata => new GoalId(metadata.GoalId)));
        return kernel;
    }

    private static ConductLoopDependencyMetadata ReadConductLoopDependencyMetadata(
        string goalId,
        string goalStatus,
        string? executionDirectory)
    {
        if (executionDirectory is null)
        {
            return new ConductLoopDependencyMetadata(goalId, goalStatus, IsLanded: false);
        }

        var journal = ConductorTickStepLedger.CountTerminalJournalCheck(
            () => TerminalGoalJournalMetadataCache.Read(executionDirectory, new GoalId(goalId)));
        var isLanded = journal.IsLanded;
        var status = !isLanded &&
            journal.IsRetiredDisposition &&
            !IsConductLoopTerminalWithoutLandingStatus(goalStatus)
                ? "Retired"
                : goalStatus;
        return new ConductLoopDependencyMetadata(goalId, status, isLanded);
    }

    private static bool IsConductLoopTerminalWithoutLandingStatus(string status) =>
        status.Equals(GoalStatus.Failed.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Cancelled.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals(GoalStatus.Superseded.ToString(), StringComparison.OrdinalIgnoreCase) ||
        status.Equals("Retired", StringComparison.OrdinalIgnoreCase);

    private sealed record ConductLoopDependencyMetadata(
        string GoalId,
        string Status,
        bool IsLanded);
}
