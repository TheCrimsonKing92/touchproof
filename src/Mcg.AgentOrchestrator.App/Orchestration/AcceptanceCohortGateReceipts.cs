using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceCohortGateReceipts
{
    internal static AcceptanceCohortReceipt Save(
        CohortAcceptanceStore store,
        AcceptanceCohortIdentity identity,
        AcceptanceCohortGateClassification? classification,
        Stopwatch gateClock,
        Func<DateTimeOffset> utcNow,
        IReadOnlyList<string> failedChecks,
        int? gateExitCode,
        IReadOnlyList<string> gateTestResultPaths,
        IReadOnlyList<AcceptanceCohortCheckEvidence>? checks = null,
        AcceptanceVerificationResult? verification = null)
    {
        gateClock.Stop();
        if (classification is null)
            throw new InvalidOperationException("Acceptance cohort gate ended without a structured classification.");

        return store.SaveGateReceipt(new AcceptanceCohortReceipt(
            $"cohort-receipt-v2-{identity.Value[(AcceptanceCohortIdentity.Version.Length + 1)..]}",
            identity,
            classification.Outcome,
            utcNow(),
            checked((long)gateClock.Elapsed.TotalMilliseconds),
            failedChecks,
            GateExitCode: gateExitCode,
            GateTestResultPaths: gateTestResultPaths,
            ValidForLanding: verification is not null && classification.Outcome == AcceptanceCohortGateOutcome.Passed,
            InfrastructureReasonCode: classification.InfrastructureReasonCode,
            InfrastructureDetail: classification.InfrastructureDetail)
        {
            GateChecks = checks?.Select(check => check.BindToCohort(identity)).ToArray() ?? [],
            GateAttemptId = verification?.AttemptId,
            GateLanePlanPath = verification?.GateLanePlanPath
        });
    }
}
