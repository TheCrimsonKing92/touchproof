using System.Globalization;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProviderHoldHistoryStartupSeed
{
    internal const string PhaseName = "provider-hold-history-seed";

    internal static void Apply(AgentOrchestratorKernel kernel, IOrchestratorStateRepository repository,
        Action<string> emit, ConductorStartupHeartbeat? heartbeat = null, TimeProvider? timeProvider = null)
    {
        var time = timeProvider ?? TimeProvider.System;
        var started = time.GetTimestamp();
        using var phase = BeginHeartbeat(heartbeat);
        try
        {
            var rows = repository.ListModelFitHistoryAsync().GetAwaiter().GetResult();
            var candidates = ProviderHoldHistory.SeedCandidates(rows,
                kernel.Goals.Select(goal => goal.Id.Value).ToHashSet(StringComparer.Ordinal));
            if (candidates.Count == 0) return;
            var evidence = repository.ReadProviderHoldSeedEvidenceAsync(candidates.Select(id => new GoalId(id)).ToArray())
                .GetAwaiter().GetResult();
            var required = ProviderHoldSeedPruning.RequiredGoals(candidates, evidence, kernel.ProviderHoldHistory, kernel.Goals);
            var loaded = required.Count == 0 ? new AgentOrchestratorKernel() :
                repository.LoadGoalsAsync(required.Select(id => new GoalId(id)).ToArray()).GetAwaiter().GetResult();
            var history = new ProviderHoldHistory();
            if (required.Count > 0)
            {
                history.CopyFrom(kernel.ProviderHoldHistory);
                history.Retain(loaded.Goals, kernel.Goals);
            }
            var elapsedMs = Math.Max(0, time.GetElapsedTime(started).Ticks / TimeSpan.TicksPerMillisecond);
            var line = $"PROVIDER_HOLD_HISTORY candidates={candidates.Count} retained={history.Retained.Count} goals={string.Join(",", history.Retained.Select(goal => goal.Id.Value[..8]))} elapsedMs={elapsedMs.ToString(CultureInfo.InvariantCulture)} loaded={loaded.Goals.Count}";
            // Emission is part of seeding: a throwing sink must leave the kernel unchanged too.
            emit(line);
            kernel.ProviderHoldHistory.CopyFrom(history);
        }
        catch (Exception exception)
        {
            emit($"PROVIDER_HOLD_HISTORY seed-failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static IDisposable? BeginHeartbeat(ConductorStartupHeartbeat? heartbeat)
    {
        try { return heartbeat?.Begin(PhaseName); }
        catch (Exception) { return null; } // Visibility must not change the seed's outcome.
    }
}
