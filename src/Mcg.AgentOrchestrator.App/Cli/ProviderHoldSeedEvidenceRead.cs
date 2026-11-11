using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class ProviderHoldSeedEvidenceRead
{
    internal static IReadOnlyList<ProviderUsageGoalEvidence>? TryRead(
        IOrchestratorStateRepository repository, IReadOnlyCollection<GoalId> candidates)
    {
        try
        {
            return repository.ReadProviderHoldSeedEvidenceAsync(candidates).GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A failed optimization cannot prove any candidate safe to skip.
            // The full read owns snapshot quarantine and can still seed the remaining goals.
            return null;
        }
    }
}
