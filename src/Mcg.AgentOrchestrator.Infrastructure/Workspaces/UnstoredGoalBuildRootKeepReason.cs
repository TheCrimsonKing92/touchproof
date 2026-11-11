namespace Mcg.AgentOrchestrator.Infrastructure;

internal enum UnstoredGoalBuildRootKeepReason
{
    GoalStored,
    NotGoalName,
    NotContained,
    InspectionDeferred,
    RegisteredOwnedRoot,
    LeaseUnreadable,
    LeaseAbsent,
    WithinWindow,
    ForeignStore,
    OwnerLive,
    TreeUnreadable,
    BudgetExhausted,
    Held,
    RemovalFailed
}
