using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceAttemptManifestIdentity
{
    internal static string? Capture(ConductorParallelAcceptanceCandidate candidate,
        string attemptId, string executionDirectory, string? projectHomeDirectory)
    {
        try
        {
            return GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(
                GoalWorktrees.TryResolve(executionDirectory, candidate.Goal.Id) ?? executionDirectory,
                candidate.ScopePaths, projectHomeDirectory);
        }
        catch (Exception exception)
        {
            // Provenance capture must not prevent the attempt from recording its verifier's
            // authoritative result. Unknown provenance stays null; it is never a matching identity.
            Console.Error.WriteLine(
                $"ACCEPTANCE_ATTEMPT_MANIFEST_IDENTITY_UNAVAILABLE goal={candidate.Goal.Id.Value} " +
                $"attempt={attemptId} exception={exception.GetType().Name} " +
                $"message={exception.Message.Replace('\r', ' ').Replace('\n', ' ')}");
            return null;
        }
    }
}
