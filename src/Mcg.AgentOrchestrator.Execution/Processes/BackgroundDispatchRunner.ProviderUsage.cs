using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed partial class BackgroundDispatchRunner
{
    internal ClaudeTranscriptUsageReader ClaudeTranscriptUsage { get; init; } = ClaudeTranscriptUsageReader.CreateDefault();

    internal ClaudeTranscriptUsageResult ResolveDispatchProviderUsage(TaskDispatchRecord? dispatch, string standardOutputPath)
    {
        var codex = NormalizeStructuredCodexOutput(dispatch, standardOutputPath);
        if (codex is not null)
            return new(codex.Usage, codex.UsageUnavailableReason);

        if (dispatch?.WorkerProviderKind == ProviderKind.AnthropicClaudeCli)
        {
            return ClaudeTranscriptUsage.ReadDispatch(dispatch);
        }

        return new(null, "unsupported");
    }

    private static TaskSpec GetTaskAfterReceiptlessUsage(
        AgentOrchestratorKernel kernel, GoalId goalId, TaskId taskId, DispatchRefreshOutcome outcome)
    {
        var task = kernel.GetTask(goalId, taskId);
        if (outcome.Verification is not null && outcome.ReceiptlessUsageAttemptAt is { } attemptAt)
        {
            var matches = task.DispatchHistory.Where(candidate => candidate.DispatchedAt == attemptAt).Take(2).ToArray();
            // Telemetry must not prevent reconciliation when its attempt is missing or ambiguous.
            if (matches.Length == 1 && matches[0].ContextPackageReceipt is null)
                kernel.RecordDispatchProviderUsage(goalId, taskId, attemptAt,
                    DispatchProviderUsage.From(outcome.ProviderUsage, outcome.ProviderUsageUnavailableReason));
        }
        return kernel.GetTask(goalId, taskId);
    }
}
