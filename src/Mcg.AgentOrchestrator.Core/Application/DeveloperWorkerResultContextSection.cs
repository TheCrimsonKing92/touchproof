namespace Mcg.AgentOrchestrator.Core;

// Projects a candidate's Developer report without granting its claims evidence authority.
internal static class DeveloperWorkerResultContextSection
{
    private const string Heading = "## Developer WORKER_RESULT for the candidate under review";
    private const string Authority = "This is the Developer's report; check it against the diff. It is not evidence that any claim is true.";
    private const int SectionBudget = 6_000;
    private const int HardCap = 12_000;
    private const int ProseBudget = 2_000;

    internal static IReadOnlyList<string> Build(Goal goal, TaskSpec task, string? candidateCommit)
    {
        if (task.RequiredRole is not (AgentRole.Reviewer or AgentRole.Tester))
        {
            return [];
        }

        var candidate = string.IsNullOrWhiteSpace(candidateCommit) ? null : candidateCommit.Trim();
        var reports = goal.Tasks
            .Where(source => source.RequiredRole == AgentRole.Developer &&
                source.Status is WorkTaskStatus.Completed or WorkTaskStatus.Failed or WorkTaskStatus.Cancelled)
            .SelectMany(source => source.VerificationHistory.Select(verification => new
            {
                TaskNumber = TaskDisplayNumber.Resolve(goal, source.Id),
                Verification = verification,
                Commit = ReviewerEvidenceBriefSection.ResolveVerifiedCommit(source, verification)
            }))
            .OrderByDescending(report => report.Verification.CompletedAt)
            .ThenByDescending(report => report.TaskNumber)
            .ToArray();

        // Undispatched briefs without a candidate or Developer history retain their existing shape.
        if (string.IsNullOrEmpty(candidate) && reports.Length == 0)
        {
            return [];
        }

        var selected = string.IsNullOrEmpty(candidate) ? null : reports.FirstOrDefault(report =>
            string.Equals(report.Commit?.Trim(), candidate, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            var newest = reports.FirstOrDefault(report => !string.IsNullOrWhiteSpace(report.Commit));
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
        if (!TryLocateBlock(output, out var start, out var endMarkerStart, out var end))
        {
            return [$"{framing}{Environment.NewLine}Complete WORKER_RESULT unavailable for this verification.", string.Empty];
        }

        var block = output[start..end];
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

    private static bool TryLocateBlock(string output, out int start, out int endMarkerStart, out int end)
    {
        start = endMarkerStart = end = -1;
        for (var offset = 0; offset < output.Length;)
        {
            var lineEnd = output.IndexOfAny(['\r', '\n'], offset);
            if (lineEnd < 0)
            {
                lineEnd = output.Length;
            }

            var line = output[offset..lineEnd];
            // Match the existing terminal-result convention: last opener, first following end.
            if (WorkerResultMarkers.IsWorkerResultOpener(line))
            {
                start = offset;
                endMarkerStart = end = -1;
            }
            else if (start >= 0 && end < 0 && WorkerResultMarkers.IsWorkerResultEndMarker(line))
            {
                endMarkerStart = offset;
                end = lineEnd;
            }

            offset = lineEnd;
            if (offset < output.Length && output[offset] == '\r')
            {
                offset++;
            }

            if (offset < output.Length && output[offset] == '\n')
            {
                offset++;
            }
        }

        return start >= 0 && end >= 0;
    }
}
