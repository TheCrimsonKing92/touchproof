namespace Mcg.AgentOrchestrator.Core;

/// <summary>Confirms that two candidates contain equivalent goal work, with audit evidence.</summary>
public interface ICandidatePatchEquivalence
{
    bool AreEquivalent(GoalId goal, string reviewedSha, string currentSha, out string evidence);
}
