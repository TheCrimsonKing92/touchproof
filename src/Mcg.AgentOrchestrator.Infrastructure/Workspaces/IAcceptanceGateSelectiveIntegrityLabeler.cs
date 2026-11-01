namespace Mcg.AgentOrchestrator.Infrastructure;

// Opts into querying before a write: Query must report the current on-disk label,
// including read failures. Legacy labelers retain unconditional set-and-confirm.
internal interface IAcceptanceGateSelectiveIntegrityLabeler : IWorkerIntegrityLabeler
{
}
