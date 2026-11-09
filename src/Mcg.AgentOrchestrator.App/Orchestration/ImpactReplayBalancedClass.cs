namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record ImpactReplayBalancedClass(string ClassName, string Lane, string TrxPath,
    double? Seconds, bool Scanned, bool WouldRun);
