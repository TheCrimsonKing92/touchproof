using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class WorkerBuildReceiptObservation(BuildReceiptLedger ledger)
{
    private long _cpu;
    private bool _unavailable;
    private readonly HashSet<OwnedProcessGroup> _read = [];

    internal void Capture(OwnedProcessGroup group)
    {
        if (!_read.Add(group)) return;
        try
        {
            if (group.TryReadAccounting(out var accounting)) _cpu += accounting.CpuMilliseconds;
            else _unavailable = true;
        }
        catch (Exception) { _unavailable = true; }
    }

    internal void Record(WorkerBuildInvocation invocation)
    {
        var start = invocation.Observations.FirstOrDefault(row => row.Phase == WorkerBuildPhase.CompilerStarted);
        var end = invocation.Observations.LastOrDefault(row => row.Phase == WorkerBuildPhase.Completed);
        if (start is null || end is null) return;
        ledger.Record(invocation.WorktreeRoot, new(end.At, invocation.GoalId, "worker-check", invocation.TreeDigest,
            invocation.Projects.ToArray(), Math.Max(0, (end.At - start.At).TotalMilliseconds),
            _unavailable || _read.Count == 0 ? null : _cpu,
            _unavailable || _read.Count == 0 ? "accounting-unavailable" : null, "none"));
    }
}
