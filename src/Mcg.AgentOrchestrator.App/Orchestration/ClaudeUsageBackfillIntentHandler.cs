using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class ClaudeUsageBackfillIntentHandler(Func<ITransactionalOrchestratorStateRepository> repository)
{
    internal bool Apply(AgentOrchestratorKernel kernel, OperatorIntentRecord intent, out string summary)
    {
        if (intent.GoalId != OperatorIntentScopes.Workspace)
            throw new InvalidOperationException("claude-usage-backfill-workspace-scope-required");
        var payload = JsonSerializer.Deserialize<ClaudeUsageBackfillOperatorIntentPayload>(intent.PayloadJson, OperatorIntentJson.Options);
        if (payload?.Fills is null || payload.Fills.Any(fill => fill is null ||
                string.IsNullOrWhiteSpace(fill.GoalId) || fill.InputTokens < 0 ||
                fill.CachedInputTokens < 0 || fill.OutputTokens < 0))
            throw new InvalidOperationException("claude-usage-backfill-invalid-payload");

        var results = new List<string>();
        foreach (var group in payload.Fills.GroupBy(fill => fill.GoalId))
        {
            var id = new GoalId(group.Key);
            if (kernel.Goals.Any(goal => goal.Id == id))
            {
                // Working-set recorder writes persist at the next goal checkpoint or loop exit.
                results.AddRange(group.Select(fill => ApplyFill(kernel, id, fill)));
                continue;
            }
            try
            {
                var outcomes = repository().TransactGoalAsync<IReadOnlyList<string>>(
                    OperatorIntentVerbs.ClaudeUsageBackfill, id, (stored, _) =>
                    {
                        if (stored is null)
                            return Task.FromResult<(bool, GoalSnapshot?, IReadOnlyList<string>)>(
                                (false, null, group.Select(_ => "goal-missing").ToArray()));
                        var scratch = new AgentOrchestratorKernel();
                        scratch.ReplaceGoalWithSnapshot(stored);
                        var applied = group.Select(fill => ApplyFill(scratch, id, fill)).ToArray();
                        var changed = applied.Contains("filled");
                        return Task.FromResult<(bool, GoalSnapshot?, IReadOnlyList<string>)>(
                            (changed, changed ? scratch.ExportGoalSnapshot(id) : null, applied));
                    }).GetAwaiter().GetResult();
                results.AddRange(outcomes);
            }
            catch (Exception)
            {
                // A failed transaction contributes no successful writes to this batch's outcome.
                results.AddRange(group.Select(_ => "store-error"));
            }
        }
        var filled = results.Count(result => result == "filled");
        var known = results.Count(result => result == "already-known");
        var failures = results.Where(result => result is not ("filled" or "already-known"))
            .GroupBy(result => result).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => $"{group.Key}={group.Count()}").ToArray();
        summary = $"filled={filled} already-known={known} failed={results.Count - filled - known} " +
            $"failed-reasons={(failures.Length == 0 ? "none" : string.Join(",", failures))}";
        return filled == 0 && known > 0 && failures.Length == 0;
    }

    private static string ApplyFill(AgentOrchestratorKernel kernel, GoalId id, ClaudeUsageBackfillFill fill)
    {
        var matches = kernel.GetGoal(id).Tasks.SelectMany(task => task.DispatchHistory
            .Where(dispatch => dispatch.DispatchedAt == fill.DispatchedAt)
            .Select(dispatch => (task.Id, Dispatch: dispatch))).Take(2).ToArray();
        if (matches.Length == 0) return "dispatch-missing";
        if (matches.Length > 1) return "dispatch-ambiguous";
        var (taskId, dispatch) = matches[0];
        if (dispatch.WorkerProviderKind != ProviderKind.AnthropicClaudeCli) return "not-claude-dispatch";
        DispatchProviderUsage? current = dispatch.ContextPackageReceipt is { } usageReceipt
            ? new(usageReceipt.InputTokens, usageReceipt.CachedInputTokens, usageReceipt.OutputTokens)
            : dispatch.ProviderUsage;
        if (IsFullyReported(current)) return "already-known";
        try
        {
            if (dispatch.ContextPackageReceipt is { } receipt)
                kernel.RecordDispatchContextPackageReceipt(id, taskId, fill.DispatchedAt,
                    receipt.WithProviderUsage(new ProviderReportedUsage(fill.InputTokens, fill.OutputTokens, fill.CachedInputTokens)));
            else
                kernel.RecordDispatchProviderUsage(id, taskId, fill.DispatchedAt, new(
                    Merge(current?.InputTokens, fill.InputTokens),
                    Merge(current?.CachedInputTokens, fill.CachedInputTokens),
                    Merge(current?.OutputTokens, fill.OutputTokens)));
            return "filled";
        }
        catch (InvalidOperationException)
        {
            return "dispatch-write-failed";
        }
    }

    internal static bool IsFullyReported(DispatchProviderUsage? usage) => usage is not null &&
        usage.InputTokens.State == ProviderUsageState.Reported &&
        usage.CachedInputTokens.State == ProviderUsageState.Reported &&
        usage.OutputTokens.State == ProviderUsageState.Reported;

    private static ProviderUsageValue Merge(ProviderUsageValue? current, long count) =>
        current?.State == ProviderUsageState.Reported ? current : ProviderUsageValue.Reported(count);
}
