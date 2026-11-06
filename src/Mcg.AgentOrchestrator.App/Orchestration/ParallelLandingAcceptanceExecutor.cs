using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns one acceptance run's mutation lease, preparation and terminal result.
// Driver policy and environment checks remain at the call site.
internal static class ParallelLandingAcceptanceExecutor
{
    internal static ConductorParallelAcceptanceRunResult Execute(
        ConductorParallelAcceptanceCandidate candidate,
        Func<IDisposable?> acquireMutationLease,
        Func<ConductorAdvanceResult> mutationHeld,
        Func<ConductorParallelAcceptanceRunResult?> prepare,
        Func<ConductorParallelAcceptanceCandidate> refreshCandidate,
        Func<ConductorParallelAcceptanceCandidate, AcceptanceVerificationSummary> verify)
    {
        var effectiveCandidate = candidate;
        var evidenceMutationLease = acquireMutationLease();
        if (evidenceMutationLease is null)
        {
            return ConductorParallelAcceptanceRunResult.Early(
                candidate,
                mutationHeld(),
                null);
        }
        ConductorParallelAcceptanceRunResult result;
        try
        {
            var early = prepare();
            if (early is not null)
            {
                return early;
            }

            effectiveCandidate = refreshCandidate();
            result = ConductorParallelAcceptanceRunResult.Accepted(
                effectiveCandidate,
                verify(effectiveCandidate));
        }
        catch (Exception ex)
        {
            result = ConductorParallelAcceptanceRunResult.Fault(effectiveCandidate, ex);
        }
        finally
        {
            try
            {
                evidenceMutationLease.Dispose();
            }
            catch
            {
                // Cleanup cannot replace a computed acceptance result. Store-backed leases emit
                // typed failure evidence and retain their owner-qualified row for expiry recovery.
            }
        }

        return result;
    }
}
