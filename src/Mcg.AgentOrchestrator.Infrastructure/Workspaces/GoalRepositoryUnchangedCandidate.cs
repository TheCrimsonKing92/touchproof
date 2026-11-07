using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// An aggregate still contains repositories with no patch. Their clean revision is part of the set.
internal static class GoalRepositoryUnchangedCandidate
{
    internal static (bool Ok, CandidateIdentity? Identity, string Reason) Compute(string path,
        Func<string, IReadOnlyList<string>, string>? manifestIdentity, string? projectHome)
    {
        try
        {
            var head = GitCli.Run(path, "rev-parse", "--verify", "HEAD^{commit}");
            if (!head.Succeeded || head.DrainTimedOut || !head.ProcessStarted)
                return (false, null, "revision-unavailable");
            var revision = head.Output.Trim();
            var manifest = manifestIdentity is null
                ? GoalAcceptanceVerifier.ComputeEffectiveAcceptancePlanIdentity(path, [], projectHome)
                : manifestIdentity(path, []);
            if (string.IsNullOrWhiteSpace(manifest)) return (false, null, "manifest-unavailable");
            return (true, new CandidateIdentity("unchanged-" + revision, revision, manifest), string.Empty);
        }
        catch (Exception ex)
        {
            return (false, null, $"candidate-computation-failed:{ex.GetType().Name}:{ex.Message}");
        }
    }
}
