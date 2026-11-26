using System.Diagnostics;
using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProviderHoldHistoryStartupSeed
{
    internal const string PhaseName = "provider-hold-history-seed";
    internal const string CompletedLinePrefix = "STARTUP_COMPLETE phase=" + PhaseName + " ";

    internal sealed record Result(ProviderHoldHistory.SeedSelection Selection,
        ProviderHoldSeedIndexResult Index, int EvidenceGoals, long ElapsedMs);

    internal static AgentOrchestratorKernel ApplyRequired(AgentOrchestratorKernel kernel,
        IOrchestratorStateRepository repository, Action<string> emit, ConductorStartupHeartbeat heartbeat)
    {
        var result = Apply(kernel, repository, emit, heartbeat)
            ?? throw new InvalidOperationException("Provider hold history seed did not complete.");
        emit(FormattableString.Invariant(
            $"{CompletedLinePrefix}elapsed_ms={result.ElapsedMs} source_goals={result.Selection.Sources.Count} evidence_goals={result.EvidenceGoals} indexed_goals={result.Index.IndexedGoals} binding_evaluations={result.Index.Evaluations.Values.Sum(entry => entry.Count)} observation_visits={result.Index.Evaluations.Values.Sum(entry => entry.ObservationVisits)}"));
        return kernel;
    }

    internal static Result? Apply(AgentOrchestratorKernel kernel, IOrchestratorStateRepository repository,
        Action<string> emit, ConductorStartupHeartbeat? heartbeat = null, TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var started = time.GetTimestamp();
        using var phase = BeginHeartbeat(heartbeat);
        try
        {
            var historyStarted = time.GetTimestamp();
            using var process = Process.GetCurrentProcess();
            var historyCpuStarted = process.TotalProcessorTime;
            var history = repository.ReadProviderHoldSeedHistoryAsync().GetAwaiter().GetResult();
            process.Refresh();
            var historyCpuMs = (long)(process.TotalProcessorTime - historyCpuStarted).TotalMilliseconds;
            var working = kernel.Goals;
            var existing = kernel.ProviderHoldHistory.Retained;
            var workingIds = working.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal);
            var selection = ProviderHoldHistory.SelectSeedEvidence(history.Rows, history.Attempts, workingIds, history.Recoveries);
            var ids = selection.Sources.Concat(selection.Dependencies).Select(id => new GoalId(id)).ToHashSet();
            var markerGoals = history.MarkerGoals;
            ids.UnionWith(markerGoals.Where(id => !workingIds.Contains(id.Value)));
            var candidates = ids.OrderBy(id => id.Value, StringComparer.Ordinal).ToArray();
            var historyMs = Elapsed(historyStarted);
            var evidenceStarted = time.GetTimestamp();
            var discovered = history.Evidence.Select(row => row.GoalId).ToHashSet(StringComparer.Ordinal);
            var remaining = candidates.Where(id => !discovered.Contains(id.Value)).ToArray();
            var evidence = remaining.Length == 0 ? [] : ProviderHoldSeedEvidenceRead.TryRead(repository, remaining);
            var evidenceMs = Elapsed(evidenceStarted);

            // Missing sparse evidence only grants permission to read the canonical snapshots.
            var requestedIds = candidates.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            bool RequiresDispatchHistory(string id) => selection.Sources.Contains(id) || selection.Dependencies.Contains(id);
            var projected = history.Evidence.Concat(evidence ?? []).Where(row => requestedIds.Contains(row.GoalId) &&
                    (row.Tasks.Count != 0 || !RequiresDispatchHistory(row.GoalId)) &&
                    (row.Timeline.Count != 0 || !markerGoals.Contains(new GoalId(row.GoalId))))
                .GroupBy(row => row.GoalId, StringComparer.Ordinal).Where(group => group.Count() == 1)
                .ToDictionary(group => group.Key, group => ProviderHoldSeedPruning.Adapt(group.Single()), StringComparer.Ordinal);
            var canonical = Load(candidates.Where(id => !projected.ContainsKey(id.Value)).ToArray());
            var inputs = projected.Values.Concat(canonical.Values).ToArray();
            var pruneStarted = time.GetTimestamp();
            var index = ProviderHoldSeedIndex.Evaluate(working, existing, inputs, selection.Sources);
            var pruneMs = Elapsed(pruneStarted);
            // The sparse inputs preserve historical refusals and recovery witnesses. Keep full
            // snapshots for current hold owners and operator/probe marker owners.
            var holdOwners = index.Evaluations.Values.Select(entry => entry.Schedule?.Hold.SourceGoalId)
                .OfType<GoalId>().ToHashSet();
            var hydrate = index.RequiredGoals.Where(projected.ContainsKey).Select(id => new GoalId(id))
                .Where(id => holdOwners.Contains(id) || markerGoals.Contains(id))
                .ToArray();
            foreach (var (id, goal) in Load(hydrate)) canonical.Add(id, goal);
            var byId = existing.ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
            foreach (var (id, goal) in projected) byId[id] = goal;
            foreach (var (id, goal) in canonical) byId[id] = goal;
            var retained = index.RequiredGoals.Order(StringComparer.Ordinal).Select(id => byId[id]).ToArray();
            var elapsedMs = Elapsed(started);
            var line = $"PROVIDER_HOLD_HISTORY candidates={candidates.Length} retained={retained.Length} goals={string.Join(",", retained.Select(goal => goal.Id.Value[..Math.Min(8, goal.Id.Value.Length)]))} elapsedMs={elapsedMs.ToString(CultureInfo.InvariantCulture)} loaded={canonical.Count} historyMs={historyMs.ToString(CultureInfo.InvariantCulture)} evidenceMs={evidenceMs.ToString(CultureInfo.InvariantCulture)} pruneMs={pruneMs.ToString(CultureInfo.InvariantCulture)}";
            // Emission is part of seeding: a throwing sink must leave the kernel unchanged too.
            if (candidates.Length > 0 || existing.Count > 0)
                emit(line + FormattableString.Invariant($" historyCpuMs={historyCpuMs}") + string.Concat(history.Timings.Select(
                    timing => FormattableString.Invariant($" {timing.Key}={timing.Value}"))));
            kernel.ProviderHoldHistory.Replace(retained);
            return new(selection, index, candidates.Length, Elapsed(started));
        }
        catch (Exception exception)
        {
            emit($"PROVIDER_HOLD_HISTORY seed-failed: {exception.GetType().Name}: {exception.Message}");
            return null;
        }

        long Elapsed(long timestamp) => Math.Max(0, time.GetElapsedTime(timestamp).Ticks / TimeSpan.TicksPerMillisecond);

        Dictionary<string, Goal> Load(IReadOnlyCollection<GoalId> requested)
        {
            if (requested.Count == 0) return [];
            var ids = requested.Select(id => id.Value).ToHashSet(StringComparer.Ordinal);
            var loaded = repository.LoadGoalsAsync(requested).GetAwaiter().GetResult().Goals
                .Where(goal => ids.Contains(goal.Id.Value)).ToDictionary(goal => goal.Id.Value, StringComparer.Ordinal);
            if (loaded.Count != requested.Count || loaded.Values.Any(goal => goal.IsMetadataOnly))
                throw new InvalidDataException("Required provider hold history is unavailable.");
            return loaded;
        }
    }

    private static IDisposable? BeginHeartbeat(ConductorStartupHeartbeat? heartbeat)
    {
        try { return heartbeat?.Begin(PhaseName); }
        catch (Exception) { return null; } // Visibility must not change the seed's outcome.
    }
}
