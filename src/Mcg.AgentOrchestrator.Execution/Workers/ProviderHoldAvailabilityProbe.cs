using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

public sealed record ProviderHoldProbeResult(string Outcome, string Reason)
{
    public ProviderFailureKind FailureKind => ProviderBudgetHoldSchedules.ClassifyOutcome(Outcome);
    public DateTimeOffset? ProviderResetAt { get; init; }
}

public interface IProviderHoldAvailabilityProbe
{
    Task<ProviderHoldProbeResult> CheckAsync(ProviderBudgetExhaustionHold hold, TaskDispatchRecord dispatch,
        CancellationToken cancellationToken);
}

/// <summary>One minimal request in an empty directory, using the held dispatch's model and profile.</summary>
public sealed class ProviderHoldAvailabilityProbe(WorkerProfileCatalog profiles,
    Func<WorkerProcessRunRequest, CancellationToken, Task<WorkerProcessRunResult>>? run = null,
    TimeProvider? timeProvider = null)
    : IProviderHoldAvailabilityProbe
{
    public async Task<ProviderHoldProbeResult> CheckAsync(ProviderBudgetExhaustionHold hold, TaskDispatchRecord dispatch,
        CancellationToken cancellationToken)
    {
        var provider = WorkerProviderCatalog.Default().ResolveProfile(dispatch.WorkerName);
        if (provider.Identity.Kind == ProviderKind.AnthropicClaudeCli &&
            !string.Equals(dispatch.ClaudeApiHoldIdentity, ClaudeCliAuthProbe.HoldIdentity(
                Environment.GetEnvironmentVariable(ClaudeCredentialSource.ApiKeyEnvironmentVariable)), StringComparison.Ordinal))
            return new("failed", "held-billing-route-unavailable");
        var directory = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("mcg-provider-check"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var template = profiles.GetRequired(dispatch.WorkerName).CommandTemplate;
            // Keep xAI's established minimal-request launcher, but allow only one model turn.
            template = Regex.Replace(template, @"--max-turns\s+\d+", "--max-turns 1", RegexOptions.CultureInvariant);
            var preparation = WorkerCommandTemplate.Prepare(new(hold.SourceGoalId, hold.SourceTaskId, AgentRole.Tester,
                "Provider availability check", "Reply with exactly AVAILABLE. Do not use tools or read or write files."),
                dispatch.WorkerName, template, directory, new Dictionary<string, string?>
                {
                    ["workingDirectory"] = directory, ["subscriptionModelName"] = dispatch.ModelName,
                    ["subscriptionReasoningEffort"] = "low", ["permissionMode"] = "plan",
                    ["sandboxMode"] = "read-only"
                });
            var command = preparation.Command;
            if (dispatch.ClaudeCredentialSourceDirectory is { Length: > 0 } credential)
                command = "$env:CLAUDE_CONFIG_DIR='" + credential.Replace("'", "''") + "'; " + command;
            var request = new WorkerProcessRunRequest(command, directory, TimeSpan.FromSeconds(30),
                StandardInput: "Reply with exactly AVAILABLE. Do not use tools or read or write files.", RequireOwnedProcessGroup: true);
            WorkerProcessRunResult result;
            try { result = await (run ?? WorkerProcessRunner.RunBufferedAsync)(request, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return new("failed", cancellationToken.IsCancellationRequested ? "cancelled" : "timeout"); }
            if (result.TimedOut || result.CaptureLimited) return new("failed", result.TimedOut ? "timeout" : "output-capture-limited");
            var outcome = new WorkerProviderOutcome(result.ExitCode, result.StandardOutput, result.StandardError);
            var evidence = ProviderHoldEvidence.Read(outcome, (timeProvider ?? TimeProvider.System).GetUtcNow());
            if (evidence.Kind == ProviderFailureKind.AuthenticationRequired)
                return new("authentication-required", "provider-sign-in-required");
            if (evidence.Kind == ProviderFailureKind.BudgetExhausted)
            {
                if (hold.ModelName is null && evidence.ScopedModel(dispatch) is not null)
                    return new("failed", "quota-refusal-scoped-to-probe-model");
                return new("exhausted", "provider-budget-exhausted") { ProviderResetAt = evidence.ResetAt };
            }
            var answer = result.StandardOutput.Trim();
            if (provider.Identity.UsesCodexExitFileBehavior &&
                !CodexJsonlUsageParser.TryParseCompletedAnswer(result.StandardOutput, out answer, out _))
                return new("failed", "incomplete-provider-answer");
            if (result.ExitCode == 0 && answer.Trim() == "AVAILABLE")
                return new("available", "minimal-request-succeeded");
            return new("failed", $"{provider.ParseOutcome(outcome)}:exit={result.ExitCode}");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
