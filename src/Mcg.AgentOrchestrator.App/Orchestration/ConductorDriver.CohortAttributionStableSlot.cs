using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorDriver
{
    internal const int CohortAttributionStableSlotRoundCount = 20;
    internal Func<string, CancellationToken, DotnetBuildEnvironmentLease>? CohortPartitionStableSlotLeaseSource { get; set; }

    private AcceptanceVerificationResult RunCohortPartitionAttempt(
        IGoalAcceptanceVerifier verifier,
        string partitionPath,
        AcceptanceCohortMemberBinding member,
        AcceptanceCohortIdentity identity,
        ConductEventLogWriter gateProgressEventWriter,
        CancellationToken cancellationToken,
        AcceptanceAttributionLaneScope? scope,
        CohortAttributionProgressIdentity? progressIdentity)
    {
        using var lease = CohortPartitionStableSlotLeaseSource is { } source
            ? source(identity.Value, cancellationToken)
            : _parallelAcceptanceAttemptCoordinator.AcquireCohortStableSlotLeaseInRounds(identity.Value,
                $"cohort-partition:goal-{member.GoalId.Value[..8]}", CohortAttributionStableSlotRoundCount, cancellationToken)
                ?? throw new CohortStableSlotsDeferredException(identity.Value);
        var options = CreateCohortAttributionExecutionOptions(gateProgressEventWriter, identity, member, scope, progressIdentity);
        AcceptanceGateProgress? last = null;
        var observe = options.ProgressSink;
        options = options with { ProgressSink = progress => { last = progress; observe?.Invoke(progress); } };
        AcceptanceVerificationResult? result = null;
        try
        {
            result = AcceptanceExecutionRunner.RunAttempt(
                verifier, partitionPath, member.GoalId, member.LandingPaths,
                lease.Environment.BuildPermitIndex, lease, cancellationToken, options);
            return result;
        }
        finally
        {
            if (last is not null) observe?.Invoke(last with { Phase = "attribution-complete",
                Outcome = result is null ? "unknown" : result.Passed ? "passed" : "failed" });
        }
    }
}
