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
        var summaries = ConductorTickStepLedger.CountMetadataRows(
            ConductorTickStepLedger.MeasureKernelReload(ConductorTickStepLedger.GoalMetadataList,
                () => stateRepository.ListConductLoopGoalMetadataAsync().GetAwaiter().GetResult()));
        var terminalDependencyMetadata = ConductorTickStepLedger.MeasureKernelReload(
            ConductorTickStepLedger.TerminalDependencyMetadataRead, () => summaries
                .Where(summary => ConductLoopGoalStatus.IsTerminal(summary.Status))
                .Select(summary => ReadConductLoopDependencyMetadata(summary, executionDirectory))
                .ToDictionary(metadata => metadata.GoalId, StringComparer.Ordinal));
        var hydratedIds = summaries
            .Where(summary =>
                !ConductLoopGoalStatus.IsTerminal(summary.Status) &&
                !summary.Status.Equals(GoalStatus.Parked.ToString(), StringComparison.OrdinalIgnoreCase))
            .Select(summary => new GoalId(summary.Id))
            .Concat((additionalHydratedGoalIds ?? [])
                .Select(id => new GoalId(id)))
            .Distinct()
            .ToArray();
        var kernel = ConductorTickStepLedger.CountGoalReload(
            GoalReloadMemory.LoadAsync(stateRepository, hydratedIds, reloadMemory,
                ConductorTickStepLedger.KernelReloadTimingSink()).GetAwaiter().GetResult());
        return ConductorTickStepLedger.MeasureKernelReload(ConductorTickStepLedger.KernelMerge,
            () => MergeConductLoopDependencyStatuses(kernel, summaries, terminalDependencyMetadata, hydratedIds, executionDirectory));
    }

    private static AgentOrchestratorKernel MergeConductLoopDependencyStatuses(
        AgentOrchestratorKernel kernel,
        IReadOnlyList<GoalSummary> summaries,
        Dictionary<string, ConductLoopDependencyMetadata> terminalDependencyMetadata,
        GoalId[] hydratedIds,
        string? executionDirectory)
    {
        kernel.MarkKnownDependencyGoalStatuses(summaries.Select(summary =>
            new KeyValuePair<GoalId, string>(
                new GoalId(summary.Id),
                terminalDependencyMetadata.TryGetValue(summary.Id, out var metadata)
                    ? metadata.Status
                    : summary.Status)));
        kernel.MarkKnownCompletedDependencyGoals(terminalDependencyMetadata.Values
            .Where(metadata => metadata.IsLanded)
            .Select(metadata => new GoalId(metadata.GoalId)));

        var loadedIds = hydratedIds.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
        var missingDependencyIds = kernel.Goals
            .SelectMany(goal => goal.DependsOn)
            .Select(id => id.Value)
            .Where(id => !loadedIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missingDependencyIds.Length == 0)
        {
            return kernel;
        }

        var missingDependencySet = missingDependencyIds.ToHashSet(StringComparer.Ordinal);
        var missingDependencySummaries = summaries
            .Where(summary => missingDependencySet.Contains(summary.Id))
            .ToArray();
        var missingDependencyMetadata = missingDependencySummaries
            .Select(summary => ReadConductLoopDependencyMetadata(summary, executionDirectory))
            .ToArray();
        kernel.MarkKnownDependencyGoalStatuses(missingDependencyMetadata.Select(metadata =>
            new KeyValuePair<GoalId, string>(new GoalId(metadata.GoalId), metadata.Status)));
        var completedDependencyIds = missingDependencyMetadata
            .Where(metadata => metadata.IsLanded)
            .Select(metadata => new GoalId(metadata.GoalId))
            .ToArray();
        kernel.MarkKnownCompletedDependencyGoals(completedDependencyIds);
        return kernel;
    }

    private static ConductLoopDependencyMetadata ReadConductLoopDependencyMetadata(
        GoalSummary summary,
        string? executionDirectory)
    {
        if (executionDirectory is null || !ConductLoopGoalStatus.IsTerminal(summary.Status))
        {
            return new ConductLoopDependencyMetadata(summary.Id, summary.Status, IsLanded: false);
        }

        var journal = ConductorTickStepLedger.CountTerminalJournalCheck(() => TerminalGoalJournalMetadataCache.Read(executionDirectory, new GoalId(summary.Id)));
        var isLanded = journal.IsLanded;
        var status = !isLanded &&
            journal.IsRetiredDisposition &&
            !IsConductLoopTerminalWithoutLandingStatus(summary.Status)
                ? "Retired"
                : summary.Status;
        return new ConductLoopDependencyMetadata(summary.Id, status, isLanded);
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
