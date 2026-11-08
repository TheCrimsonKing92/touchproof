using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record MultiRepositoryTupleLandingResult(MultiRepositoryLandingRecord? Record = null,
    LandingResult? HomeLanding = null, ConductorAdvanceOutcome.Held? Hold = null);
