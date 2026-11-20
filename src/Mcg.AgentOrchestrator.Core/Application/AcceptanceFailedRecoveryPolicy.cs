namespace Mcg.AgentOrchestrator.Core;

public static class AcceptanceFailedRecoveryPolicy
{
    public const string FailedGateBlocker = "acceptance-gate-failed";
    public const string EnvironmentHoldNextAction = "Restore the contracted tool; the conductor retries automatically.";

    public static AcceptanceFailedRecoveryDecision? Decide(Goal goal, bool replacementGateLive = false)
    {
        if (goal.Status != GoalStatus.AcceptanceFailed)
        {
            return null;
        }

        var failure = goal.LatestAcceptanceFailure;
        var environmental = failure?.IsEnvironmentalApparatus == true;
        var prefix = goal.Id.Value[..8];
        var blockers = new List<string> { FailedGateBlocker };
        var cases = new List<string>();

        if (replacementGateLive)
        {
            blockers.Add("acceptance-gate-in-flight");
            cases.Add("replacement acceptance gate in flight");
        }

        if (failure is null)
        {
            blockers.Add("acceptance-receipt-missing");
            cases.Add("missing receipt");
        }
        else if (environmental)
        {
            cases.Add("environmental apparatus failure");
        }
        else
        {
            blockers.Add("acceptance-candidate-code-failure");
            cases.Add("candidate-code failure");
        }

        var capExhausted = goal.OperatorAcceptanceRegateCount >= Goal.OperatorAcceptanceRegateCap;
        if (capExhausted)
        {
            blockers.Add("acceptance-retry-cap-exhausted");
            cases.Add($"exhausted retry cap ({goal.OperatorAcceptanceRegateCount}/{Goal.OperatorAcceptanceRegateCap})");
        }

        var unmetTasks = goal.Tasks
            .Select((task, index) => (Task: task, Number: index + 1))
            .Where(item => item.Task.Status is not (WorkTaskStatus.Completed or WorkTaskStatus.Cancelled) ||
                item.Task.Status == WorkTaskStatus.Completed && item.Task.LastVerification is not { Succeeded: true })
            .Select(item => item.Task.Status == WorkTaskStatus.Completed
                ? $"task {item.Number} (Completed, verification did not pass)"
                : $"task {item.Number} ({item.Task.Status})")
            .ToArray();
        if (unmetTasks.Length > 0)
        {
            blockers.Add("acceptance-task-prerequisites-unmet");
            cases.Add($"unmet task prerequisites: {string.Join(", ", unmetTasks)}");
        }

        var checks = failure is { FailedChecks.Count: > 0 } ? string.Join(", ", failure.FailedChecks) : "unavailable";
        var branch = Available(failure?.BranchHeadSha);
        var main = Available(failure?.MainHeadSha);
        var candidateDetails = $"failed checks: {checks}, branch head: {branch}, main head: {main}";
        var details = $"{candidateDetails}, attempt: {Available(failure?.AttemptId)}, solo failure receipt: {Available(failure?.SoloFailureReceipt?.Evidence.Path)}";
        var reason = $"Acceptance gate failed ({string.Join(", ", cases)}). {details}.";
        var offersRetry = environmental && !capExhausted && unmetTasks.Length == 0;
        string command;

        if (replacementGateLive)
        {
            command = string.Empty;
        }
        else if (offersRetry)
        {
            var retryReason = BuildRetryReason(checks, branch, main);
            command = $"acceptance-retry {prefix} \"{retryReason}\" --confirm-acceptance-retry";
        }
        else
        {
            command = string.IsNullOrWhiteSpace(failure?.AttemptId)
                ? "gate-stats failures"
                : $"gate-stats failure --attempt {failure.AttemptId}";
            reason += $" After triage, use adjudicate --goal {prefix} <task#> reopen-regate|route|close --text-file <path> --evidence <ref> or recover {prefix} <note>.";
        }

        var monitorAction = goal.CurrentHold?.State switch
        {
            "environment-hold" => EnvironmentHoldNextAction,
            "author-owner-question" or "steward-owner-question" => $"Review the pending owner question with attention show {prefix}.",
            _ => $"When no replacement gate is running, use {command}."
        };
        var monitorMessage = replacementGateLive
            ? $"Acceptance gate failed. Replacement acceptance gate in flight. {candidateDetails}."
            : $"Acceptance gate failed. {monitorAction} {candidateDetails}.";
        return new AcceptanceFailedRecoveryDecision(
            blockers, reason, command, offersRetry && !replacementGateLive, failure, details, monitorMessage);
    }

    private static string Available(string? value) => string.IsNullOrWhiteSpace(value) ? "unavailable" : value;

    private static string BuildRetryReason(string checks, string branch, string main)
    {
        static string CommandText(string value, int limit) => new string(value.Take(limit)
            .Select(character => char.IsLetterOrDigit(character) || character is ' ' or '_' or '.' or ',' ? character : ' ')
            .ToArray()).Trim();

        return $"environmental apparatus failure in {CommandText(checks, 100)} at branch {CommandText(branch, 12)} main {CommandText(main, 12)}";
    }
}

public sealed record AcceptanceFailedRecoveryDecision(
    IReadOnlyList<string> Blockers,
    string Reason,
    string NextSafeCommand,
    bool OffersAcceptanceRetry,
    AcceptanceFailureSummary? Failure,
    string FailureDetails,
    string MonitorMessage);
