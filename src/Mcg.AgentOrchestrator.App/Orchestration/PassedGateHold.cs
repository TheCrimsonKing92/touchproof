namespace Mcg.AgentOrchestrator.App.Orchestration;

// A current landing obligation attached to a passed receipt, not a new goal status.
internal sealed record PassedGateHold(
    PassedGateHoldCategory Category, string Rule, string Evidence,
    string AttemptId, string Candidate, IReadOnlyList<string> ResolvingVerbs)
{
    internal string? ReceiptPath { get; init; }
    internal string CategoryText => Category switch
    {
        PassedGateHoldCategory.GateTestObligation => "gate test obligation",
        PassedGateHoldCategory.CriterionEvidenceObligation => "criterion evidence obligation",
        PassedGateHoldCategory.LandingDenylist => "landing denylist",
        PassedGateHoldCategory.PendingHumanWait => "pending human wait",
        _ => throw new ArgumentOutOfRangeException(nameof(Category))
    };

    internal string Describe() => $"passed gate, held: {CategoryText}\n" +
        $"Attempt: {AttemptId}\nReceipt: {ReceiptPath ?? "not recorded"}\nCandidate: {Candidate}\nRule: {Rule}\nEvidence: {Evidence}\n" +
        string.Join("\n", ResolvingVerbs.Select(verb => $"Resolve: {verb}"));
}
