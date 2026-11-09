using System.Text;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerGoalVerifiedSectionReader(
    Func<TaskVerificationRecord, CancellationToken, Task<string?>>? artifact,
    Func<GoalId, string, GoalPromotionFact?, CancellationToken, Task<OwnerGoalAcceptanceEvidence>>? acceptance)
{
    internal async Task<(OwnerGoalSummarySection Section, OwnerGoalPreviewEvidence? Preview)> VerifiedAsync(Goal goal, string? candidate,
        GoalPromotionFact? landed, CancellationToken token)
    {
        var (outcome, expectedMain, unavailable) = await ReadAcceptanceAsync(goal, candidate, landed, token);
        var (obligations, applicable) = ApplicableObligations(goal, candidate);
        var lines = applicable.Select(item => $"{item.DisplayLabel}: {item.State}; {item.RequiredScope}; receipt {item.ReceiptId}").Take(10).ToList();
        var sourceLines = new List<string>(lines);
        void AddLine(string value, bool bound = false)
        { sourceLines.Add(value); lines.Add(bound ? OwnerGoalSummarySectionReader.BoundItem(value) : value); }
        foreach (var item in obligations.Where(item => item.PlannerSources is { Count: > 0 } && !item.HasSatisfiedEvidenceFor(candidate)))
            AddLine($"{item.DisplayLabel}: {item.State}; owner={item.Owner}; scope={item.RequiredScope}; " +
                $"Planner sources={string.Join(",", item.PlannerSources!.Select(source => source.Identity.StableId))}", bound: true);
        var failed = applicable.Any(item => item.State == CriterionEvidenceState.Failed);
        var all = obligations.Length > 0 && obligations.All(item => item.HasSatisfiedEvidenceFor(candidate));
        var state = candidate is null ? "unavailable — candidate unknown" : failed ? "failed — applicable criterion receipt" :
            all ? "passed — applicable criterion receipts" : "pending — changed candidate lacks complete applicable receipts";
        OwnerGoalPreviewEvidence? previewEvidence = null;
        if (outcome is not null)
        {
            var passed = outcome.Status == GoalOperationStatus.Completed &&
                (string.Equals(outcome.AcceptanceOutcome, "passed", StringComparison.OrdinalIgnoreCase) ||
                 GoalOperationJournalGatePassedOutcome.Matches(outcome));
            if (passed || outcome.Status == GoalOperationStatus.Failed || outcome.AcceptanceOutcome == "failed")
                previewEvidence = new(OwnerGoalPreviewEvidenceKind.Gate, passed, outcome.At,
                    passed ? null : outcome.FailedCheckNames?.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
            state = passed
                ? failed ? state : obligations.Length == 0 || all ? "passed — applicable full acceptance" :
                    "pending — full acceptance passed; criterion evidence outstanding"
                : failed ? state : outcome.Status == GoalOperationStatus.Failed || outcome.AcceptanceOutcome == "failed"
                    ? "failed — applicable full acceptance" : "pending — acceptance " + outcome.AcceptanceOutcome;
            AddLine($"Full acceptance: {outcome.AcceptanceOutcome}; event {outcome.IdempotencyKey}; main {outcome.MainHeadSha}; {outcome.At:O}", bound: true);
            foreach (var check in (outcome.FailedCheckNames ?? []).Take(5)) AddLine(check, bound: true);
        }
        if (unavailable is not null) AddLine("Acceptance evidence unavailable — " + unavailable);
        if (candidate is not null && goal.RetainedAcceptanceFailure is { } failure &&
            string.Equals(failure.BranchHeadSha, candidate, StringComparison.OrdinalIgnoreCase) &&
            (outcome is null || failure.OccurredAt > outcome.At))
        {
            if (expectedMain is not null && string.Equals(failure.MainHeadSha, expectedMain, StringComparison.OrdinalIgnoreCase))
            {
                previewEvidence = new(OwnerGoalPreviewEvidenceKind.Gate, false, failure.OccurredAt,
                    failure.FailedChecks.FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)));
                state = "failed — recorded acceptance (main " + failure.MainHeadSha + ")";
                foreach (var check in failure.FailedChecks.Take(5)) AddLine(check, bound: true);
            }
            else
                AddLine("Retained acceptance failure " + (expectedMain is null ? "applicability unavailable" : "superseded") +
                    "; recorded main " + (failure.MainHeadSha ?? "unknown") + "; expected main " + (expectedMain ?? "unavailable"));
        }
        if (obligations.Any(item => item.Owner == CriterionEvidenceOwner.Operator && !item.HasSatisfiedEvidenceFor(candidate)))
            AddLine("Operator obligation outstanding");
        if (candidate is not null)
            foreach (var line in FocusedExecutionLines(goal, candidate)) AddLine(line);
        if (previewEvidence is null && candidate is not null)
            previewEvidence = FocusedPreview(goal, candidate);
        return (new(state, lines.Take(20).ToArray(), "criterion obligations/acceptance run events/retained failure; candidate " + (candidate ?? "unknown"),
            Math.Max(0, applicable.Length - 10) + Math.Max(0, lines.Count - 20), SourceItems: sourceLines.Take(20).ToArray()), previewEvidence);
    }

    private async Task<(GoalOperationJournalEntry? Outcome, string? ExpectedMain, string? Unavailable)> ReadAcceptanceAsync(
        Goal goal, string? candidate, GoalPromotionFact? landed, CancellationToken token)
    {
        GoalOperationJournalEntry? outcome = null;
        string? expectedMain = null;
        string? unavailable = null;
        try
        {
            if (candidate is not null && acceptance is not null)
            {
                var evidence = await acceptance(goal.Id, candidate, landed, token).WaitAsync(token);
                if (!OwnerGoalSummarySectionReader.FullRevision(evidence.MainRevision)) throw new InvalidDataException("Integration revision unavailable.");
                expectedMain = evidence.MainRevision;
                outcome = evidence.Outcome;
            }
            if (outcome is not null && (outcome.GoalId != goal.Id ||
                !outcome.HasCandidate(candidate!, expectedMain!)))
            { outcome = null; unavailable = "mismatched acceptance identity"; }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { unavailable = ex.GetType().Name; }
        return (outcome, expectedMain, unavailable);
    }

    private static (CriterionEvidenceObligation[] Obligations, CriterionEvidenceObligation[] Applicable) ApplicableObligations(
        Goal goal, string? candidate)
    {
        var obligations = goal.CriterionEvidenceObligations.Where(item =>
            item.CriterionVersion == goal.AuthoritativeRefinedSpecVersion?.Version && item.State != CriterionEvidenceState.Repaired).ToArray();
        var applicable = obligations.Where(item => item.CandidateSha == candidate && candidate is not null &&
            (item.State == CriterionEvidenceState.Failed || item.HasSatisfiedEvidenceFor(candidate)) &&
            (item.ExpectedCandidateSha is null || item.ExpectedCandidateSha == candidate)).ToArray();
        return (obligations, applicable);
    }

    private static IEnumerable<string> FocusedExecutionLines(Goal goal, string candidate)
    {
        foreach (var task in goal.Tasks)
            foreach (var receipt in task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? [])
                         .GroupBy(item => item.ReceiptId).Select(group => group.Last())
                         .Where(item => item.Accepted && item.CandidateSha == candidate).Take(5))
                yield return $"Focused execution: {(FindingEvidenceReceiptSelector.SuppliedReceiptsPassAtCandidate(task, candidate, [receipt.ReceiptId]) ? "passed" : "failed")}; receipt {receipt.ReceiptId}; full acceptance tracked separately";
    }

    private static OwnerGoalPreviewEvidence? FocusedPreview(Goal goal, string candidate)
    {
        OwnerGoalPreviewEvidence? previewEvidence = null;
        var focused = goal.Tasks.SelectMany(task => task.VerificationHistory.SelectMany(record =>
                (record.FindingEvidenceReceipts ?? []).Select(receipt => (Task: task, Record: record, Receipt: receipt))))
            .Where(item => item.Receipt.Accepted &&
                string.Equals(item.Receipt.CandidateSha, candidate, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => item.Record.CompletedAt).FirstOrDefault();
        if (focused.Receipt is not null)
            previewEvidence = new(OwnerGoalPreviewEvidenceKind.Focused,
                FindingEvidenceReceiptSelector.SuppliedReceiptsPassAtCandidate(focused.Task, candidate, [focused.Receipt.ReceiptId]),
                focused.Record.CompletedAt);
        return previewEvidence;
    }

    internal async Task<string?> ReadArtifactAsync(TaskVerificationRecord record, CancellationToken token)
    {
        if (artifact is not null)
        {
            var text = await artifact(record, token).WaitAsync(token);
            return text is not null && Encoding.UTF8.GetByteCount(text) <= OwnerGoalChangeSummaryQuery.MaxArtifactBytes ? text : null;
        }
        if (record.AuthoritativeStandardOutput is { } output && Encoding.UTF8.GetByteCount(output) <= OwnerGoalChangeSummaryQuery.MaxArtifactBytes &&
            (record.StandardOutputPath is null || output.Contains(PlannerOutputContract.DurablePlanBeginMarker, StringComparison.Ordinal) || output.Contains("WORKER_RESULT:", StringComparison.Ordinal)))
            return output;
        if (record.StandardOutputPath is not { } path) return null;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > OwnerGoalChangeSummaryQuery.MaxArtifactBytes) return null;
        var bytes = new byte[OwnerGoalChangeSummaryQuery.MaxArtifactBytes + 1];
        var length = 0;
        while (length < bytes.Length)
        {
            var read = await stream.ReadAsync(bytes.AsMemory(length), token);
            if (read == 0) break;
            length += read;
        }
        return length > OwnerGoalChangeSummaryQuery.MaxArtifactBytes ? null : new UTF8Encoding(false, true).GetString(bytes, 0, length);
    }
}
