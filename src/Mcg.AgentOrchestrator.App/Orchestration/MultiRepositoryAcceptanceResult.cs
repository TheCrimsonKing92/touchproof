using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record MultiRepositoryAcceptanceResult(
    ConductorAdvanceOutcome.Held? Hold, AcceptanceVerificationSummary? Failure = null);
