using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record BuildReceiptSite(string WorktreePath, string? GoalId, string Origin, string TreeDigest,
    BuildReceiptLedger? Ledger = null, TreeBuildOutputReuse? Reuse = null)
{
    internal static BuildReceiptSite Create(IAcceptanceRunExecutionContext? context, string worktree, string? goalId) =>
        new(worktree, goalId, GateHeartbeatRunClass.Classify(context) == GateHeartbeatRunClass.FocusedEvidence
            ? "pre-review-evidence" : GateHeartbeatRunClass.ResolveRunId(context)?.StartsWith("train:", StringComparison.Ordinal) == true
                ? "train" : "gate", BuildReceipt.Digest(context?.TreeRevision),
            Reuse: TreeBuildOutputReuse.ForProduction(worktree, context));

    internal IDisposable Observe(DotnetTestBuildPhase phase, out BuildCpuMeter meter)
    {
        meter = new();
        return new Observation(this, phase, meter);
    }

    private sealed class Observation(BuildReceiptSite site, DotnetTestBuildPhase phase, BuildCpuMeter meter) : IDisposable
    {
        private readonly Stopwatch _wall = Stopwatch.StartNew();
        public void Dispose()
        {
            var reused = phase.TreeOutputRestore?.Hit == true;
            if (meter.Runs == 0 && !reused) return;
            var (cache, refusalReason) = BuildReceiptSiteCacheStatus.Resolve(phase);
            (site.Ledger ?? BuildReceiptLedger.Default).Record(site.WorktreePath,
                new(DateTimeOffset.UtcNow, site.GoalId, site.Origin, site.TreeDigest,
                    reused && phase.BuildArguments.ElementAtOrDefault(2) is { Length: > 0 } target
                        ? [target] : meter.Projects.ToArray(),
                    _wall.Elapsed.TotalMilliseconds,
                    meter.UnavailableReason is null ? meter.CpuMs : null, meter.UnavailableReason,
                    cache, RefusalReason: refusalReason));
        }
    }
}
