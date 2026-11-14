namespace Mcg.AgentOrchestrator.Infrastructure;

public enum UnitCommandMeasurementFailureCause
{
    CommandDidNotStart,
    TimedOut,
    ExitedNonZero,
    NotMeasurableTool,
    Other
}
