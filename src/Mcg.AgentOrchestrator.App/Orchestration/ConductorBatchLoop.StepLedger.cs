using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    private ConductorTickStepLedger? _stepLedger;
    private Action<string>? _stepProbe;
    private GitLaunchWindowCensus? _gitCensus;
    private GitLaunchWindowCensus.Window? _tickGitWindow;
    private DiagnosticTimer? _tickGitTimer;

    internal ConductorBatchLoop WithGitCensus(GitLaunchWindowCensus census)
    {
        _gitCensus = census;
        return this;
    }

    // Like janitorialPhaseProbe, this advances a deterministic injected CPU fixture
    // at the existing call sites without replacing any of their behavior.
    internal ConductorBatchLoop WithStepProbe(Action<string> probe)
    {
        _stepProbe = probe;
        return this;
    }

    private IDisposable BeginTickCpuAndStepLedger()
    {
        BeginTickCpuAccounting();
        _stepLedger = new(ReadProcessCpu, _stepProbe);
        var scope = ActivateDiagnosticTime(_stepLedger.Activate());
        _tickGitWindow = _gitCensus?.OpenWindow("tick");
        _tickGitTimer = _gitCensus is null ? null : StartDiagnosticTimer();
        return new TickGitScope(scope, _tickGitWindow);
    }

    private sealed class TickGitScope(IDisposable scope, GitLaunchWindowCensus.Window? window) : IDisposable
    {
        public void Dispose() { window?.Dispose(); scope.Dispose(); }
    }

    private void AddLedgerPhaseTimings(List<string> lines, int tick, string phase, TimeSpan elapsed, string detail, long cpuMs)
    {
        var ledger = _stepLedger!;
        // Sweep precedes BoardFill. Complete its buffered detail only after prewalk,
        // retaining the original phase total and every existing field verbatim.
        var sweepIndex = lines.FindIndex(line => line.StartsWith($"PHASE_TIMING tick={tick} phase=sweep ", StringComparison.Ordinal));
        if (sweepIndex >= 0)
        {
            var line = lines[sweepIndex];
            var suffix = line.LastIndexOf(" cpu_ms=", StringComparison.Ordinal);
            lines[sweepIndex] = line.Insert(suffix,
                $" sweep_steps={ledger.FormatSteps(ConductorTickStepLedger.SweepNames)} load_counters={ledger.FormatCounters()}");
        }
        lines.Add(FormatPhaseTiming(tick, phase, elapsed,
            $"{detail} prewalk_steps={ledger.FormatSteps(ConductorTickStepLedger.PrewalkNames)}", cpuMs));
    }
}
