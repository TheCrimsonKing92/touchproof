using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ManagedRebuildReceipt
{
    internal static void Record(BuildReceiptLedger? ledger, string worktree, string? goalId, string lane,
        AcceptanceCheckResult result, TimeSpan wall, DateTimeOffset now, string? tree)
    {
        if (ledger is null || !lane.EndsWith("Mtp managed project rebuild", StringComparison.Ordinal) ||
            result.TestResultIsExplicitCrossAttemptReuse) return;
        var cpu = GateLaneReceipt.FromSummary(lane, result.TestResultRunOrdinal, wall, result.ResultSummary,
            GateLoadSample.Unavailable("not-sampled")).CpuMilliseconds;
        // This lane owns builds inside its tests: its job CPU includes that test overhead.
        ledger.Record(worktree, new(now, goalId, "managed-rebuild", BuildReceipt.Digest(tree), [lane],
            wall.TotalMilliseconds, cpu.Value, cpu.UnavailableReason, "none"));
    }
}
