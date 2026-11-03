using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static class CliClaudeUsageBackfillCommand
{
    internal static bool IsCommand(IReadOnlyList<string> args) => args.Count > 0 &&
        args[0].Equals(OperatorIntentVerbs.ClaudeUsageBackfill, StringComparison.OrdinalIgnoreCase);

    internal static int Run(IReadOnlyList<string> args, OrchestratorWorkspace workspace) =>
        Run(args, () => ReadGoals(workspace), ClaudeTranscriptUsageReader.CreateDefault(),
            () => SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory),
            Console.Out, Console.Error);

    internal static int Run(IReadOnlyList<string> args, Func<IReadOnlyCollection<Goal>> loadGoals,
        ClaudeTranscriptUsageReader reader, Func<IOperatorIntentStore> intentStore, TextWriter output, TextWriter error)
    {
        try
        {
            var (since, prefix, apply, json) = Parse(args);
            IEnumerable<Goal> goals = loadGoals();
            if (prefix is not null)
            {
                var matches = goals.Where(goal => goal.Id.Value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matches.Length == 0) throw new ArgumentException($"unknown-goal: {prefix}");
                if (matches.Length > 1) throw new ArgumentException($"ambiguous-goal-prefix: {prefix}");
                goals = matches;
            }
            var fills = new List<ClaudeUsageBackfillFill>();
            var known = 0;
            var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var goal in goals)
            foreach (var dispatch in goal.Tasks.SelectMany(task => task.DispatchHistory))
            {
                if (dispatch.WorkerProviderKind != ProviderKind.AnthropicClaudeCli ||
                    since is { } cutoff && dispatch.DispatchedAt < cutoff) continue;
                if (ClaudeUsageBackfillIntentHandler.IsFullyReported(ContextUsageFigures.RecordedUsage(dispatch)))
                {
                    known++;
                    continue;
                }
                var result = reader.ReadDispatch(dispatch);
                if (result.Usage is { InputTokens: { } input, CachedInputTokens: { } cached, OutputTokens: { } tokens })
                    fills.Add(new(goal.Id.Value, dispatch.DispatchedAt, input, cached, tokens));
                else
                {
                    var reason = result.UnavailableReason.Split(':', 2)[0].Trim();
                    skipped[reason] = skipped.GetValueOrDefault(reason) + 1;
                }
            }
            OperatorIntentRecord? persisted = null;
            if (apply)
            {
                var attribution = CliPersistentStateRunner.ResolveOperatorIntentAttribution(args,
                    CliPersistentStateRunner.OperatorIntentSubmissionSource.Cli);
                var id = Guid.NewGuid().ToString("N");
                persisted = intentStore().EnqueueAsync(new OperatorIntentRecord(id, id,
                    OperatorIntentVerbs.ClaudeUsageBackfill, OperatorIntentScopes.Workspace, null,
                    JsonSerializer.Serialize(new ClaudeUsageBackfillOperatorIntentPayload(fills), OperatorIntentJson.Options), [],
                    attribution.Actor, attribution.Channel, attribution.AuthenticationAssurance, DateTimeOffset.UtcNow,
                    ActorKind: attribution.ActorKind)).GetAwaiter().GetResult();
            }
            if (json)
                output.WriteLine(JsonSerializer.Serialize(new
                {
                    filled = fills.Count, alreadyKnown = known, skipped = skipped.Values.Sum(), skippedReasons = skipped,
                    intentId = persisted?.Id,
                    poll = persisted is null ? null : $"operator-intent-status {persisted.Id}"
                }));
            else
            {
                output.WriteLine($"Claude usage backfill: filled={fills.Count} already-known={known} skipped={skipped.Values.Sum()} " +
                    $"skipped-reasons={(skipped.Count == 0 ? "none" : string.Join(",", skipped.Select(pair => $"{pair.Key}={pair.Value}")))}");
                if (persisted is not null)
                    output.WriteLine($"Operator intent queued: id={persisted.Id} verb={persisted.Verb} scope=workspace status={persisted.Status}; " +
                        $"poll with operator-intent-status {persisted.Id} (or add --wait).");
            }
            return 0;
        }
        catch (Exception ex)
        {
            error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static IReadOnlyCollection<Goal> ReadGoals(OrchestratorWorkspace workspace)
    {
        if (!File.Exists(workspace.SqliteStatePath))
            throw new FileNotFoundException("State database is missing.", workspace.SqliteStatePath);
        return SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
            .LoadAsync().GetAwaiter().GetResult().Goals;
    }

    private static (DateTimeOffset? Since, string? Goal, bool Apply, bool Json) Parse(IReadOnlyList<string> args)
    {
        DateTimeOffset? since = null;
        string? goal = null;
        var apply = false;
        var json = false;
        for (var i = 1; i < args.Count; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--apply": apply = true; break;
                case "--json": json = true; break;
                case "--goal" when i + 1 < args.Count && !string.IsNullOrWhiteSpace(args[i + 1]) && !args[i + 1].StartsWith('-'):
                    goal = args[++i];
                    break;
                case "--since" when i + 1 < args.Count &&
                    Regex.IsMatch(args[i + 1], @"^\d{4}-\d{2}-\d{2}T.+(?:Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant) &&
                    DateTimeOffset.TryParse(args[i + 1], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date):
                    since = date;
                    i++;
                    break;
                default: throw new ArgumentException(CliCommandHelp.ClaudeUsageBackfillUsage);
            }
        }
        return (since, goal, apply, json);
    }
}
