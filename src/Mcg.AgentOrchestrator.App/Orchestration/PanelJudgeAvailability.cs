namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PanelJudgeAvailability
{
    internal static void ValidateSeats(IReadOnlyList<IConductorPanelJudgeRunner> judges)
    {
        if (judges.Count != 2 || judges.Any(judge => string.IsNullOrWhiteSpace(judge.Judge)) ||
            judges.Select(judge => judge.Judge).Distinct(StringComparer.Ordinal).Count() != 2)
            throw new ArgumentException("Panel requires two distinct seat identities.");
    }

    internal static string? ConfigurationReason(IReadOnlyList<IConductorPanelJudgeRunner> judges)
    {
        var valid = judges.Where(judge => judge.BindingError is null).ToArray();
        if (valid.Length != 2) return null; // Preserve each seat's specific binding failure.
        return valid.Any(judge => !ProviderBoundPanelJudgeBinding.Supported(judge.WorkerProfileName))
            ? "provider-identity-unavailable"
            : valid.Select(judge => judge.WorkerProfileName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != 2
                ? "provider-not-distinct" : null;
    }

    internal static string? ObservedReason(PanelProcessResult result, bool providerFault, PanelJudgeOutcome outcome)
    {
        if (result.Failure is not null) return result.Failure;
        if (result.TimedOut) return "judge-timeout";
        if (outcome == PanelJudgeOutcome.Valid) return null;
        if (result.ExitCode != 0 || providerFault)
            return !string.IsNullOrWhiteSpace(result.Stderr) ? result.Stderr :
                !string.IsNullOrWhiteSpace(result.Stdout) ? result.Stdout : $"judge-exit:{result.ExitCode}";
        return PanelV0Contract.Token(outcome);
    }
}
