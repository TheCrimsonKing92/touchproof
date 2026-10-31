namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed class AcceptanceGateIntegrityPreparationException(string path, Exception? inner = null)
    : InvalidOperationException($"Failed to prepare acceptance workspace '{path}' for Medium-integrity gate checks.", inner);
