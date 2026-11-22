namespace Mcg.AgentOrchestrator.Core;

// Projects a candidate's Developer report without granting its claims evidence authority.
internal static class DeveloperWorkerResultContextSection
{
    private const string Heading = "## Developer WORKER_RESULT for the candidate under review";
    private const string Authority = "This is the Developer's report; check it against the diff. It is not evidence that any claim is true.";
    private const int SectionBudget = 6_000;
    private const int HardCap = 12_000;
    private const int ProseBudget = 2_000;

    internal sealed record CandidateReport(TaskSpec Task, int TaskNumber, TaskVerificationRecord Verification, string? Commit);

    internal sealed record CandidateReportSelection(string? Candidate, CandidateReport? Selected, CandidateReport? NewestWithCommit, bool HasReports);

    internal static CandidateReportSelection SelectCandidateReport(Goal goal, string? candidateCommit)
    {
        var candidate = string.IsNullOrWhiteSpace(candidateCommit) ? null : candidateCommit.Trim();
        var reports = goal.Tasks
            .Where(source => source.RequiredRole == AgentRole.Developer &&
                source.Status is WorkTaskStatus.Completed or WorkTaskStatus.Failed or WorkTaskStatus.Cancelled)
            .SelectMany(source => source.VerificationHistory.Select(verification => new CandidateReport(
                source, TaskDisplayNumber.Resolve(goal, source.Id), verification,
                ReviewerEvidenceBriefSection.ResolveVerifiedCommit(source, verification))))
            .OrderByDescending(report => report.Verification.CompletedAt)
            .ThenByDescending(report => report.TaskNumber)
            .ToArray();

        var selected = candidate is null ? null : reports.FirstOrDefault(report =>
            string.Equals(report.Commit?.Trim(), candidate, StringComparison.OrdinalIgnoreCase));
        return new CandidateReportSelection(candidate, selected,
            selected is null ? reports.FirstOrDefault(report => !string.IsNullOrWhiteSpace(report.Commit)) : null,
            reports.Length > 0);
    }

    internal static IReadOnlyList<string> Build(Goal goal, TaskSpec task, string? candidateCommit,
        CandidateReportSelection? selection = null)
    {
        if (task.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester))
        {
            return [];
        }

        selection ??= SelectCandidateReport(goal, candidateCommit);
        var candidate = selection.Candidate;
        // Undispatched briefs without a candidate or Developer history retain their existing shape.
        if (candidate is null && !selection.HasReports)
        {
            return [];
        }

        var selected = selection.Selected;
        if (selected is null)
        {
            var newest = selection.NewestWithCommit;
            var newestCommit = newest is null
                ? "no completed Developer commit was found"
                : $"newest Developer commit found: {newest.Commit} (Task {newest.TaskNumber})";
            return [$"{Heading} — no completed Developer verification matches candidate {candidate ?? "unavailable"}; {newestCommit}. Developer reports are checked against the diff, not evidence a claim is true.", string.Empty];
        }

        var framing = string.Join(Environment.NewLine,
            Heading,
            $"Source: Task {selected.TaskNumber} Developer verification completed {selected.Verification.CompletedAt:O}; verified commit {selected.Commit}; candidate {candidate}.",
            Authority);
        var output = selected.Verification.AuthoritativeStandardOutput ?? selected.Verification.StandardOutput;
        if (!WorkerResultMarkers.TryLocateBlock(output, out var start, out var endMarkerStart, out var end))
        {
            return [$"{framing}{Environment.NewLine}Complete WORKER_RESULT unavailable for this verification.", string.Empty];
        }

        var block = output[start..end];
        if (task.RequiredRole == AgentRole.Tester)
        {
            var fullResultLine = string.IsNullOrWhiteSpace(selected.Verification.StandardOutputPath)
                ? "Full result file unavailable."
                : $"Full result file: {selected.Verification.StandardOutputPath}";
            return [string.Join(Environment.NewLine,
                new[] { framing }.Concat(DeveloperWorkerResultProjection.Project(block)).Append(fullResultLine)), string.Empty];
        }

        var proseCount = Math.Min(start, ProseBudget);
        // Try the longest suffix first; all framing and omission text counts toward the budget.
        while (proseCount > 0 && Render(block, proseCount).Length > SectionBudget)
        {
            proseCount--;
        }

        var section = Render(block, proseCount);
        if (section.Length > HardCap)
        {
            // Keep the start and original end marker; only the end of the block body is removed.
            var bodyLength = endMarkerStart - start;
            var endMarker = output[endMarkerStart..end];
            var keep = Math.Min(bodyLength, HardCap - framing.Length);
            string cutBlock;
            do
            {
                cutBlock = block[..keep] + Environment.NewLine +
                    $"WORKER_RESULT cut: {bodyLength - keep} characters omitted from the end" +
                    Environment.NewLine + endMarker;
                section = Render(cutBlock, 0);
                if (section.Length <= HardCap)
                {
                    break;
                }

                keep -= section.Length - HardCap;
            } while (keep >= 0);
        }

        return [section, string.Empty];

        string Render(string renderedBlock, int retainedProse)
        {
            var text = framing + Environment.NewLine + renderedBlock;
            if (retainedProse > 0)
            {
                text += Environment.NewLine +
                    $"Developer's own account (last {retainedProse} characters before WORKER_RESULT):" +
                    Environment.NewLine + output[(start - retainedProse)..start];
            }

            if (start > retainedProse)
            {
                text += Environment.NewLine +
                    $"Omitted: {start - retainedProse} characters of Developer output before WORKER_RESULT.";
            }

            return text;
        }
    }
}
