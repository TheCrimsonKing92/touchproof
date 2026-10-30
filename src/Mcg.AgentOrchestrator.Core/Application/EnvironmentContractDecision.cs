namespace Mcg.AgentOrchestrator.Core;

public sealed record EnvironmentContractDecision(string Project, string Tool, string Fingerprint, string Reason)
{
    public const string StageName = "environment-contract";
    public PolicyDecisionRecord ToRecord() => new(StageName, "Hold", 1, "probe-failed", Reason,
        [new("project", Project), new("tool", Tool), new("fingerprint", Fingerprint)]);
}
