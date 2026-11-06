using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class LandingExecutor
{
    internal sealed record LoadedLandingDenylist(LandingDenylist Denylist, string Source);

    private static readonly BoundedInsertionOrderMemo<string, LoadedLandingDenylist> LandingDenylistMemo =
        new(256, GoalWorktrees.CommitFactKeyComparer);

    private static string InvalidDenylistSource(Exception exception) =>
        "invalid:" + exception.Message.Replace('\r', ' ').Replace('\n', ' ');

    internal static LoadedLandingDenylist LoadLandingDenylist(string executionDirectory, string boundMainSha)
    {
        var key = GitRunnerOverride.Value is null && GoalWorktrees.IsFullObjectId(boundMainSha)
            ? GoalWorktrees.CommitFactKey(executionDirectory, boundMainSha) : null;
        if (key is not null && LandingDenylistMemo.TryGet(key, out var cached)) return Copy(cached);

        // Rules and paths expose arrays through IReadOnlyList. Never expose the stored arrays.
        static LoadedLandingDenylist Copy(LoadedLandingDenylist loaded) => new(
            LandingDenylist.Parse(JsonSerializer.Serialize(new
            {
                version = 1,
                rules = loaded.Denylist.Rules.Select(rule => new
                    { id = rule.Id, reason = rule.Reason, paths = rule.Paths })
            }), loaded.Source), loaded.Source);

        LoadedLandingDenylist Remember(LoadedLandingDenylist loaded)
        {
            if (key is null) return loaded;
            var snapshot = Copy(loaded);
            LandingDenylistMemo.Store(key, snapshot);
            return Copy(snapshot);
        }

        // Listing the immutable tree distinguishes absence from a failed git invocation.
        // Read failures escape before mutation; only malformed configuration uses the default.
        var listing = RunGit(executionDirectory, "ls-tree", "--name-only", boundMainSha, "--", "config/landing-denylist.json");
        if (listing.ExitCode != 0 || listing.DrainTimedOut || !listing.ProcessStarted)
            throw new InvalidOperationException($"Cannot read landing denylist on main: {listing.Error}");
        if (string.IsNullOrWhiteSpace(listing.Output))
            return Remember(new(LandingDenylist.BuiltInDefault, LandingAdmissionReceipt.BuiltInDefaultSource));
        var content = RunGit(executionDirectory, "show", $"{boundMainSha}:config/landing-denylist.json");
        if (content.ExitCode != 0 || content.DrainTimedOut || !content.ProcessStarted)
            throw new InvalidOperationException($"Cannot read landing denylist on main: {content.Error}");
        var source = $"main:{boundMainSha}";
        try
        {
            return Remember(new(LandingDenylist.Parse(content.Output, source), source));
        }
        catch (FormatException exception) { return new(LandingDenylist.BuiltInDefault, InvalidDenylistSource(exception)); }
    }

    private static LandingAdmissionReceipt BuildLandingAdmission(
        LoadedLandingDenylist loaded, string candidateSha, LandingDenylistOwnerGate.Result evaluation)
    {
        if (!evaluation.Approved) throw new InvalidOperationException("Cannot record admission for unapproved protected content.");
        var matches = evaluation.Matches.Select(match => $"{match.RuleId}={match.Path}").ToArray();
        return matches.Length == 0
            ? LandingAdmissionReceipt.Evaluated(candidateSha, loaded.Source, matches)
            : LandingAdmissionReceipt.OwnerApproved(candidateSha, loaded.Source, matches);
    }
}
