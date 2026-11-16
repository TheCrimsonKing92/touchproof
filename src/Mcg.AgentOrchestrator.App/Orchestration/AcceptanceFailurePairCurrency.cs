namespace Mcg.AgentOrchestrator.App.Orchestration;

// Currency describes evidence for a complete branch/main pair, never a partial identity.
internal static class AcceptanceFailurePairCurrency
{
    internal enum Currency { Current, Superseded, Unknown }

    internal const string Stage = "acceptance-failure-pair-currency";
    internal const string Unknown = "acceptance-failure-pair-unknown";
    internal const string Superseded = "acceptance-failure-pair-superseded";
    internal const string NotReady = "superseded-acceptance-failure-not-ready";

    internal static Currency Classify(string? recordedBranch, string? recordedMain,
        string? currentBranch, string? currentMain)
    {
        if (string.IsNullOrWhiteSpace(recordedBranch) || string.IsNullOrWhiteSpace(recordedMain) ||
            string.IsNullOrWhiteSpace(currentBranch) || string.IsNullOrWhiteSpace(currentMain))
            return Currency.Unknown;
        return string.Equals(recordedBranch, currentBranch, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(recordedMain, currentMain, StringComparison.OrdinalIgnoreCase)
            ? Currency.Current : Currency.Superseded;
    }
}
