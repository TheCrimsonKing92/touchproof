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
    public bool? KeepCaptureFiles { get; init; } = null;
    public Action<int>? ResumeObserver { get; init; } = null;
    public bool ResolveBareCommand { get; init; }
    internal IReadOnlyDictionary<string, string?>? ChildEnvironmentOverrides { get; init; }
    internal JunitCommandEnvironmentInputs? CommandEnvironment { get; init; }
    internal bool SuppressDiagnosticCaptures { get; init; }
}
