namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class ConductorSelfRelaunch
{
    internal const string SupervisorDelegatedReason = "delegated-to-continuity-supervisor";

    internal static Func<ConductorSelfRelaunchRequest, ConductorSelfRelaunchResult> CreateSupervisorDelegated() =>
        _ => new ConductorSelfRelaunchResult(
            true,
            null,
            null,
            new ConductorLoopHandoffResult(
                Started: true,
                ProcessId: null,
                StdoutPath: null,
                StderrPath: null,
                Reason: SupervisorDelegatedReason));
}
