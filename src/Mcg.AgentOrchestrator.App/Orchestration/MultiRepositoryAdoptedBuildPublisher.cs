using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One invocation publishes one adopted tuple set; holds are isolated to the declared key.
internal sealed class MultiRepositoryAdoptedBuildPublisher(
    MultiRepositoryIntegrationBases bases, string genericPatterns, string ownerPatterns,
    Action<string> append, Action<string> attention,
    Func<string, string[], GitCli.GitResult>? git = null)
{
    internal sealed record Outcome(string Repository, string Commit, bool Pushed);

    internal IReadOnlyList<Outcome> Publish(IReadOnlyList<MultiRepositoryLandingRecord> records)
    {
        var outcomes = new List<Outcome>();
        var attempted = new HashSet<(string Key, string Commit, string Branch)>();
        foreach (var record in records)
        foreach (var key in record.Order.Where(key => key != "default"))
        {
            if (!record.LandedCommits.TryGetValue(key, out var commit) || string.IsNullOrWhiteSpace(commit))
            {
                Hold(key, "unresolved", "configured-branch", "landed-commit-missing");
                continue;
            }
            RepositoryLandingTarget target;
            try { target = bases.Resolve(key); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException)
            {
                Hold(key, commit, "configured-branch", "repository-unresolved");
                continue;
            }
            if (!attempted.Add((key, commit.ToLowerInvariant(), target.Branch))) continue;
            var service = new AdoptedBuildPushService(target.Root, genericPatterns, ownerPatterns,
                append, attention, git, key);
            outcomes.Add(new(key, commit, service.Push(commit, "origin", target.Branch)));
        }
        return outcomes;

        void Hold(string key, string commit, string branch, string reason)
        {
            if (!attempted.Add((key, commit.ToLowerInvariant(), branch))) return;
            AdoptedBuildPushService.ReportHeld(commit, "origin", branch, reason, key, append, attention);
            outcomes.Add(new(key, commit, false));
        }
    }
}
