namespace Mcg.AgentOrchestrator.Infrastructure;

// State describes applied transitions only. A proposed state is committed after successful actuation.
internal sealed record ExecutorSlotControllerState(int ObservedSlots, ExecutorSlotControllerSettings Settings,
    int StableWindows = 0, ExecutorSlotWatermark? CompletedThrough = null,
    ExecutorSlotWindow? PreviousWindow = null, ExecutorSlotTrial? Trial = null,
    int LastDirection = -1);
