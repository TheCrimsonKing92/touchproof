using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal sealed class PromotionFactBackfillPlanner
{
    internal sealed record UnknownGoal(string GoalId, string Reason);
    internal sealed record ConflictGoal(string GoalId, IReadOnlyList<string> MainRevisions);
    internal sealed record Plan(IReadOnlyList<GoalPromotionFact> PlannedGoals,
        IReadOnlyList<string> AlreadyRecordedGoals, IReadOnlyList<UnknownGoal> UnknownGoals,
        IReadOnlyList<ConflictGoal> ConflictGoals, int MalformedLines);
    private sealed record Landing(DateTimeOffset At, string? Main, string? Candidate, string? Source);

    internal static Plan Build(IReadOnlyList<GoalSummary> goals, ISet<string> storedGoalIds,
        string eventsDirectory, string executionDirectory, string integrationBranch,
        Func<string, string[], GitCli.GitResult>? runGit = null)
    {
        runGit ??= (directory, args) => GitCli.Run(directory, args);
        var planned = new List<GoalPromotionFact>();
        var recorded = new List<string>();
        var unknown = new List<UnknownGoal>();
        var conflicts = new List<ConflictGoal>();
        var history = new Dictionary<string, (string? Parent, string? Reason)>(StringComparer.Ordinal);
        var malformed = 0;
        foreach (var goal in goals.Where(g => g.Status == nameof(GoalStatus.Completed)).OrderBy(g => g.Id, StringComparer.Ordinal))
        {
            if (goal.PromotionFact is not null) { recorded.Add(goal.Id); continue; }
            if (storedGoalIds.Contains(goal.Id)) { unknown.Add(new(goal.Id, "stored-fact-unreadable")); continue; }
            var events = ReadEvents(eventsDirectory, goal.Id, ref malformed);
            if (events.Count == 0) { unknown.Add(new(goal.Id, "no-landing-event")); continue; }
            var revisions = events.Select(e => e.Main).Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s!.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            if (revisions.Length > 1) { conflicts.Add(new(goal.Id, revisions)); continue; }
            if (revisions.Length == 0) { unknown.Add(new(goal.Id, "no-main-revision")); continue; }
            var landing = events.Where(e => !string.IsNullOrWhiteSpace(e.Main)).OrderBy(e => e.At).First();
            if (string.IsNullOrWhiteSpace(landing.Candidate)) { unknown.Add(new(goal.Id, "no-candidate-revision")); continue; }
            var main = revisions[0];
            if (!FullRevision(main) || !FullRevision(landing.Candidate))
            { unknown.Add(new(goal.Id, "revision-not-full-hex")); continue; }
            if (!history.TryGetValue(main, out var ancestry))
            {
                if (!runGit(executionDirectory, ["merge-base", "--is-ancestor", main, integrationBranch]).Succeeded)
                    ancestry = (null, "main-revision-not-reachable");
                else
                {
                    var parent = runGit(executionDirectory, ["rev-parse", "--verify", main + "^1"]);
                    ancestry = parent.Succeeded && FullRevision(parent.Output.Trim())
                        ? (parent.Output.Trim(), null) : (null, "main-revision-has-no-parent");
                }
                history.Add(main, ancestry);
            }
            if (ancestry.Reason is { } reason) { unknown.Add(new(goal.Id, reason)); continue; }
            try
            {
                planned.Add(new(goal.Id, landing.Candidate, ancestry.Parent!, main,
                    events.Min(e => e.At).ToUniversalTime(), GoalPromotionFact.ReconstructedProvenancePrefix + landing.Source));
            }
            catch (ArgumentException) { unknown.Add(new(goal.Id, "invalid-source")); }
        }
        return new(planned, recorded, unknown, conflicts, malformed);
    }

    private static bool FullRevision(string value) => value.Length is 40 or 64 && value.All(Uri.IsHexDigit);

    private static List<Landing> ReadEvents(string directory, string goalId, ref int malformed)
    {
        var events = new List<Landing>();
        // Goal ids come from the store; only read the file directly under the events directory.
        if (Path.GetFileName(goalId) != goalId) return events;
        var path = Path.Combine(directory, goalId + ".jsonl");
        if (!File.Exists(path)) return events;
        foreach (var line in File.ReadLines(path))
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                var root = document.RootElement;
                if (Text(root, "eventType") != "GoalLanded") continue;
                if (Text(root, "goalId") != goalId) { malformed++; continue; }
                var at = root.GetProperty("timestamp").GetDateTimeOffset();
                if (at == default) { malformed++; continue; }
                var integrate = Text(root, "integrateSha");
                events.Add(new(at, Text(root, "mainSha"),
                    string.IsNullOrWhiteSpace(integrate) ? Text(root, "branchTip") : integrate, Text(root, "source")));
            }
            catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
            { malformed++; }
        }
        return events;
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) ? value.GetString() : null;
}
