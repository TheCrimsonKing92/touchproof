using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Host-side scope: never opts out the conductor/CLI itself or changes attempt-process policy.
internal sealed class HostedVerifierRunQos : IDisposable
{
    private readonly AcceptanceChildQosRun run;
    private readonly string? summaryKind;
    private readonly string? logPath;
    private bool disposed;

    private HostedVerifierRunQos(string statePath, string? summaryKind, string? logPath)
    {
        run = new(DispatchJobExecutionQos.IsEnabled(statePath));
        this.summaryKind = summaryKind;
        this.logPath = logPath;
    }

    internal static HostedVerifierRunQos Begin(OrchestratorWorkspace workspace, string? summaryKind = null) =>
        new(workspace.SqliteStatePath, summaryKind, workspace.ConductEventsLogPath);

    internal static HostedVerifierRunQos Begin(string statePath) => new(statePath, null, null);

    public void Dispose()
    {
        if (disposed) return;
        run.Dispose();
        disposed = true;
        if (summaryKind is null) return;
        try
        {
            new ConductEventLogWriter(logPath!).Append("build-qos", null,
                $"BUILD_QOS kind={summaryKind} opted_out={run.OptedOutCount} tried={run.TriedCount}");
        }
        catch (Exception) { /* QoS logging must preserve the verifier result. */ }
    }
}
