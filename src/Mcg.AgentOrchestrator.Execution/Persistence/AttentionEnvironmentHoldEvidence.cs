using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record AttentionEnvironmentHoldEvidence(string GoalId, GoalHoldSnapshot Hold);
