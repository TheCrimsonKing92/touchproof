using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ConductorPolicyResolution(
    ConductorAutonomyPolicy Policy,
    string Source,
    IReadOnlyList<string> Warnings)
{
    internal string FormatReload(int tick, ConductorAutonomyPolicy previousPolicy) =>
        $"POLICY_RELOAD tick={tick} source={ConductorDiagnosticText.SanitizeReason(Source)} " +
        $"{FormatPolicyValues("old", previousPolicy)} {FormatPolicyValues("new", Policy)}";

    private static string FormatPolicyValues(string prefix, ConductorAutonomyPolicy policy)
    {
        var transitions = string.Join(',', policy.TransitionMap
            .OrderBy(entry => entry.Key)
            .Select(entry => $"{entry.Key}:{entry.Value}"));

        return $"{prefix}Name={ConductorDiagnosticText.Sanitize(policy.Name)} " +
               $"{prefix}MaxConcurrentPaidWorkers={policy.MaxConcurrentPaidWorkers} " +
               $"{prefix}MaxCriterionRetries={policy.MaxCriterionRetries} {prefix}BuildRootReapIntervalSeconds={policy.BuildRootReapIntervalSeconds} " +
               $"{prefix}AutoPromoteRiskThreshold={policy.AutoPromoteRiskThreshold?.ToString() ?? "none"} " +
               $"{prefix}MaxEmptyOutputDispatchRetries={policy.MaxEmptyOutputDispatchRetries} " +
               $"{prefix}MaxEmptyOutputAutoRecoverCycles={policy.MaxEmptyOutputAutoRecoverCycles} " +
               $"{prefix}EmptyOutputRetryInitialDelaySeconds={policy.EmptyOutputRetryInitialDelaySeconds} " +
               $"{prefix}EmptyOutputRetryBackoffMultiplier={policy.EmptyOutputRetryBackoffMultiplier} " +
               $"{prefix}EmptyOutputRetryMaxDelaySeconds={policy.EmptyOutputRetryMaxDelaySeconds} " +
               $"{prefix}ReviewAutoRetryWarningRound={policy.ReviewAutoRetryWarningRound} " +
               $"{prefix}ReviewAutoRetryStopRound={policy.ReviewAutoRetryStopRound} " +
               $"{prefix}ReviewAutoRetryLifetimeMultiplier={policy.ReviewAutoRetryLifetimeMultiplier} " +
               $"{prefix}TransitionMap={transitions}";
    }
}
