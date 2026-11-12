namespace Mcg.AgentOrchestrator.App.Orchestration;

/// <summary>The admission failed and its exact, unclaimed dispatch has already been discarded.</summary>
internal sealed class AbortedDispatchStartException(string message, Exception innerException)
    : InvalidOperationException(message, innerException);
