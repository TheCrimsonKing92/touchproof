namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBatchLoop
{
    // Call only after the tick body completes and before any watch or idle sleep.
    private void EmitActivationTickEnd(bool emitActivationHeartbeat, int tick)
    {
        CostGoalOutcomeSweep.ObserveTick(this, _workspace, _utcNow, CurrentConductEventLogWriter.Value);
        if (emitActivationHeartbeat) EmitProgress($"TICK_END tick={tick} activation=true");
    }
}
