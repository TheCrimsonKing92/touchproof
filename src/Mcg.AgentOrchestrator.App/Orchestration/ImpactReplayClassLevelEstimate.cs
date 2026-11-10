namespace Mcg.AgentOrchestrator.App.Orchestration;

// Null cost means unavailable evidence; zero means measured work that would not be saved.
internal sealed record ImpactReplayClassLevelEstimate(double? SecondsSaved, double? NonFloorTestSeconds,
    string? UnavailableReason, double? CpuSecondsSaved = null,
    string CpuUnavailableReason = "no per-class CPU data read by impact-replay");
