using Mcg.AgentOrchestrator.Core;
using static Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed record AcceptanceProcessRequest
{
    public Action<AcceptanceProcessCleanupObservation>? CleanupObserver { get; init; } = null;
    public Action<SpawnProcessIdentity>? CommandIdentityObserver { get; init; } = null;
    public GateHeartbeatContext? HeartbeatContext { get; init; } = null;
    public Action<AcceptanceGateProgress>? ProgressSink { get; init; } = null;
    public AcceptanceGateEngineSettings? EngineSettings { get; init; } = null;
    public AcceptanceHostTestSettings.Binding? HostTestSettings { get; init; }
    public string? AttemptResultsPrefix { get; init; } = null;
    public string? GateInvocationId { get; init; } = null;
    public string? ApparatusReceiptPath { get; init; } = null;
    public TimeSpan? HeartbeatInterval { get; init; } = null;
    public TimeSpan? ProgressInterval { get; init; } = null;
    public TimeSpan? CapturePublicationInterval { get; init; } = null;
    public bool StopOnCaptureLimit { get; init; } = false;
    internal Action<string, string, Task>? CaptureObserver { get; init; }
    public bool? KeepCaptureFiles { get; init; } = null;
    public Action<int>? ResumeObserver { get; init; } = null;
    public bool ResolveBareCommand { get; init; }
    internal IReadOnlyDictionary<string, string?>? ChildEnvironmentOverrides { get; init; }
    internal JunitCommandEnvironmentInputs? CommandEnvironment { get; init; }
    internal bool SuppressDiagnosticCaptures { get; init; }

    internal Action? ObserveCaptureLimit(string stdoutPath, string stderrPath, Action? onLimitReached)
    {
        if (CaptureObserver is null) return onLimitReached;
        // Each use of the request gets an observational task, never a stop token.
        var observed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CaptureObserver(stdoutPath, stderrPath, observed.Task);
        return () =>
        {
            onLimitReached?.Invoke();
            observed.TrySetResult();
        };
    }
}
