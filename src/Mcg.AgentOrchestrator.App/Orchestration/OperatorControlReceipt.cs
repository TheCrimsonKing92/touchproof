namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OperatorControlReceipt(
    string ReceiptId,
    string GoalId,
    string ObligationId,
    string Kind,
    string Command,
    int ExitStatus,
    int PassedCount,
    int FailedCount,
    IReadOnlyList<string> TrxPaths,
    string CandidateSha,
    DateTimeOffset RecordedAt);
