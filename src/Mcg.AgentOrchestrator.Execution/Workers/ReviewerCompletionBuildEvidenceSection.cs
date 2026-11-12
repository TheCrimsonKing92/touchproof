using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class ReviewerCompletionBuildEvidenceSection
{
    internal static IReadOnlyList<string> Build(Goal goal, TaskSpec reviewer, string workingDirectory)
    {
        if (reviewer.RequiredRole != AgentRole.Reviewer) return [];

        var digest = string.Empty;
        var reason = "not a git workspace";
        var hasDigest = (Directory.Exists(Path.Combine(workingDirectory, ".git")) ||
            File.Exists(Path.Combine(workingDirectory, ".git"))) &&
            WorktreeTreeDigest.TryCompute(workingDirectory, out digest, out reason);
        var lines = new List<string>
        {
            string.Empty,
            "## Host Completion Build Evidence",
            "Host build evidence for the candidate only; not a product-test result and grants no test credit. Acceptance owns test execution.",
            $"Reviewed worktree tree digest: {(hasDigest ? digest : $"unavailable ({reason})")}."
        };

        foreach (var task in goal.Tasks.TakeWhile(task => task.Id != reviewer.Id)
                     .Where(task => task.RequiredRole is AgentRole.Developer or AgentRole.Tester))
        {
            lines.Add($"### Prior {task.RequiredRole} task {task.Id.Value}");
            var verification = task.LastVerification;
            string? tests = null;
            var hasReport = verification is not null &&
                new WorkerResultContractParser().TryParseWorkerResultContract(verification, out var fields) &&
                fields.TryGetValue("tests", out tests) && !string.IsNullOrWhiteSpace(tests);
            if (hasReport)
            {
                // Preserve the reported fact without interpreting free text as host execution evidence.
                var report = tests!.ReplaceLineEndings(" ");
                lines.Add($"- Worker-reported (self-report, not host evidence): {report[..Math.Min(report.Length, 2000)]}");
            }

            var record = task.CompletionBuildEvidence;
            if (record is null)
            {
                lines.Add("- Host completion build: unknown (no record).");
                continue;
            }

            var ownsRecord = record.GoalId == goal.Id.Value && record.TaskId == task.Id.Value;
            // Timestamps alone do not bind a worker report to this dispatch attempt.
            var sameAttempt = ownsRecord && task.LastDispatch?.DispatchedAt == record.DispatchAt &&
                verification is not null && verification.DispatchAttemptAt == record.DispatchAt &&
                verification.CompletedAt >= record.DispatchAt;
            var ordering = hasReport && sameAttempt ? "later host fact for the same task dispatch"
                : "ordering relative to worker report not established";
            lines.Add($"- Host completion build ({ordering}): {Correspondence(record, ownsRecord, hasDigest ? digest : null)}.");
            lines.Add($"  Phase: {record.Phase}; exit code: {record.BuildExitCode?.ToString() ?? "unknown"}; " +
                $"applied: {record.Applied}; custody released: {record.CustodyReleased}.");
            lines.Add($"  Projects: {string.Join(", ", record.Projects)}");
            lines.Add($"  Tree digest: {(string.IsNullOrWhiteSpace(record.TreeDigest) ? "unavailable" : record.TreeDigest)}");
            lines.Add($"  Operation: {record.Id}; dispatch: {record.DispatchId}; dispatch at: {record.DispatchAt:O}");
        }
        return lines;
    }

    private static string Correspondence(CompletionBuildEvidenceOperation record, bool ownsRecord, string? currentDigest)
    {
        if (!ownsRecord) return "unknown (record ownership does not match the goal and task)";
        if (record.Phase == CompletionBuildEvidencePhase.StaleCandidate)
            return "stale: does not describe the reviewed candidate";
        if (record.Phase != CompletionBuildEvidencePhase.Success || record.BuildExitCode != 0 ||
            !record.Applied || !record.CustodyReleased)
            return "unknown (host build execution or application is incomplete or unsuccessful)";
        if (currentDigest is null) return "unknown (reviewed worktree tree digest unavailable)";
        if (!HasUsableDigest(record.TreeDigest)) return "unknown (record tree digest unavailable)";
        return string.Equals(record.TreeDigest, currentDigest, StringComparison.Ordinal)
            ? "matches reviewed candidate" : "stale: does not describe the reviewed candidate";
    }

    private static bool HasUsableDigest(string digest)
    {
        var prefix = WorktreeTreeDigest.Algorithm + ":";
        if (string.IsNullOrWhiteSpace(digest) || !digest.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var hash = digest[prefix.Length..];
        return hash.Length is 40 or 64 && hash.All(Uri.IsHexDigit);
    }
}
