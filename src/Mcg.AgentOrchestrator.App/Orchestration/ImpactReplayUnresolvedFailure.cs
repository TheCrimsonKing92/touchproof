namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayUnresolvedFailure(string Identity, string TrxPath, string Reason);
