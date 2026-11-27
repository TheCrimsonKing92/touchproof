namespace Mcg.AgentOrchestrator.Core;

public static class FindingEvidenceRequestReceiptLegacyReader
{
    public static FindingEvidenceRequestReceipt? TryRead(
        ProgressEvent evt, TaskId requestingTaskId, string candidateSha, string fingerprint, string requestGuard)
    {
        const string prefix = "finding-evidence disposition=";
        if (evt.Kind != ProgressKind.FindingEvidenceRequestRecorded ||
            evt.TaskId != requestingTaskId ||
            evt.FindingEvidenceRequest is not null ||
            evt.PreTesterEvidence is not null ||
            !evt.Message.StartsWith(prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var dispositionEnd = evt.Message.IndexOf(';', prefix.Length);
        if (dispositionEnd < 0)
            return null;

        var disposition = evt.Message[prefix.Length..dispositionEnd];
        var (decision, hold) = disposition switch
        {
            "baseline-arm-absent" => (FindingEvidenceRequestReceiptDecision.BaselineArmAbsent, FindingEvidenceRequestReceiptHold.UnconfirmedRedRouting),
            "candidate-rerun-requested" => (FindingEvidenceRequestReceiptDecision.CandidateRerunRequested, (FindingEvidenceRequestReceiptHold?)null),
            "candidate-rerun-green" => (FindingEvidenceRequestReceiptDecision.CandidateRerunGreen, (FindingEvidenceRequestReceiptHold?)null),
            "candidate-rerun-red" => (FindingEvidenceRequestReceiptDecision.CandidateRerunRed, FindingEvidenceRequestReceiptHold.Escalation),
            "candidate-rerun-unusable" => (FindingEvidenceRequestReceiptDecision.CandidateRerunUnusable, FindingEvidenceRequestReceiptHold.Escalation),
            _ => ((FindingEvidenceRequestReceiptDecision?)null, (FindingEvidenceRequestReceiptHold?)null)
        };
        if (decision is null)
            return null;

        // The historical guard includes the candidate SHA and round. Require the entire guard
        // at its recorded position before treating the remaining fields as receipt evidence.
        var header = $"{prefix}{disposition}; {requestGuard}";
        if (!evt.Message.StartsWith(header, StringComparison.Ordinal) ||
            (evt.Message.Length > header.Length && evt.Message[header.Length] != ';'))
        {
            return null;
        }

        var fields = evt.Message.AsSpan(header.Length);
        var receiptId = ReadField(ref fields, "; receipt_id=");
        var originalReceiptId = ReadField(ref fields, "; original_receipt_id=");
        return new FindingEvidenceRequestReceipt(
            requestGuard, FindingEvidenceRequestReceipt.BuildRerunGuard(requestGuard),
            requestingTaskId.Value, candidateSha, fingerprint, decision.Value, receiptId, originalReceiptId, hold);
    }

    private static string? ReadField(ref ReadOnlySpan<char> fields, string prefix)
    {
        if (!fields.StartsWith(prefix, StringComparison.Ordinal))
            return null;

        var value = fields[prefix.Length..];
        var end = value.IndexOf(';');
        fields = end < 0 ? [] : value[end..];
        return (end < 0 ? value : value[..end]).ToString();
    }
}

internal static class FindingEvidenceRequestTimeline
{
    internal static FindingEvidenceRequestReceipt? FindBaselineArmAbsent(
        IReadOnlyList<ProgressEvent> timeline, TaskId taskId, string candidateSha, string fingerprint, string guard) =>
        ReadNewestFirst(timeline, taskId, candidateSha, fingerprint, guard)
            .FirstOrDefault(receipt => receipt.Decision == FindingEvidenceRequestReceiptDecision.BaselineArmAbsent);

    internal static (FindingEvidenceRequestReceipt? Terminal, bool HasRequest) ReadRerun(
        IReadOnlyList<ProgressEvent> timeline, TaskId taskId, string candidateSha, string fingerprint, string guard, string rerunGuard)
    {
        var hasRequest = false;
        foreach (var receipt in ReadNewestFirst(timeline, taskId, candidateSha, fingerprint, guard))
        {
            if (!string.Equals(receipt.RerunGuard, rerunGuard, StringComparison.Ordinal))
                continue;

            switch (receipt.Decision)
            {
                case FindingEvidenceRequestReceiptDecision.CandidateRerunRequested:
                    hasRequest = true;
                    break;
                case FindingEvidenceRequestReceiptDecision.CandidateRerunGreen:
                case FindingEvidenceRequestReceiptDecision.CandidateRerunRed:
                case FindingEvidenceRequestReceiptDecision.CandidateRerunUnusable:
                    return (receipt, hasRequest);
            }
        }

        return (null, hasRequest);
    }

    internal static bool IsGreen(FindingEvidenceRequestReceipt terminal) => (terminal.Decision, terminal.Hold) switch
    {
        (FindingEvidenceRequestReceiptDecision.CandidateRerunGreen, null) => true,
        (FindingEvidenceRequestReceiptDecision.CandidateRerunRed or
            FindingEvidenceRequestReceiptDecision.CandidateRerunUnusable, FindingEvidenceRequestReceiptHold.Escalation) => false,
        _ => throw new InvalidDataException($"Recorded {terminal.Decision} decision has an invalid resulting hold.")
    };

    internal static string RequireReceiptId(FindingEvidenceRequestReceipt receipt, string disposition, string qualifier = "")
    {
        if (receipt.ReceiptId is null)
            throw new InvalidDataException($"Recorded {disposition} decision has no {qualifier}receipt id.");
        if (string.IsNullOrWhiteSpace(receipt.ReceiptId))
            throw new InvalidDataException($"Recorded {disposition} decision has an empty {qualifier}receipt id.");
        return receipt.ReceiptId;
    }

    private static IEnumerable<FindingEvidenceRequestReceipt> ReadNewestFirst(
        IReadOnlyList<ProgressEvent> timeline, TaskId taskId, string candidateSha, string fingerprint, string guard)
    {
        for (var index = timeline.Count - 1; index >= 0; index--)
        {
            var evt = timeline[index];
            if (evt.Kind != ProgressKind.FindingEvidenceRequestRecorded || evt.TaskId != taskId)
                continue;

            var receipt = evt.FindingEvidenceRequest ??
                FindingEvidenceRequestReceiptLegacyReader.TryRead(evt, taskId, candidateSha, fingerprint, guard);
            if (receipt?.Matches(taskId, candidateSha, fingerprint, guard) == true)
                yield return receipt;
        }
    }
}
