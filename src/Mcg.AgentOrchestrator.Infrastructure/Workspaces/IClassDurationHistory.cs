namespace Mcg.AgentOrchestrator.Infrastructure;

// Immutable, read-only predictions keyed by <ProjectLabel>:<class name>. Null means unavailable.
internal interface IClassDurationHistory
{
    double? PredictSeconds(string projectQualifiedClass);
}
