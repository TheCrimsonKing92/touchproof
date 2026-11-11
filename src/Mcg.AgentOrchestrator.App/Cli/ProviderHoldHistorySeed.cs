using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProviderHoldHistorySeed
{
    internal static void Apply(AgentOrchestratorKernel kernel, IOrchestratorStateRepository repository, Action<string> emit)
        => ProviderHoldHistoryStartupSeed.Apply(kernel, repository, emit);

    internal static void Apply(AgentOrchestratorKernel kernel, IOrchestratorStateRepository repository,
        Action<string> emit, ConductorStartupHeartbeat heartbeat, TimeProvider? timeProvider = null)
        => ProviderHoldHistoryStartupSeed.Apply(kernel, repository, emit, heartbeat, timeProvider);
}
