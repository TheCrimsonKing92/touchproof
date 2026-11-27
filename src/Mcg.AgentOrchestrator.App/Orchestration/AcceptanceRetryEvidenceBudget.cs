using System.Text;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One instance owns admission and duplicate identity for one retry note.
internal sealed class AcceptanceRetryEvidenceBudget
{
    private const int MaxLineCharacters = 2_000;
    private const int MaxFailureCharacters = 8_000;
    private const int MaxEvidenceCharacters = 32_000;
    private const string FailurePrefix = "[FAIL]";
    private readonly Dictionary<(string Name, string FirstMessageLine), string> _seen = new();
    private readonly List<(int Index, string TrxPath, AcceptanceTrxFailure Failure, string LabeledPrefix)> _failures = [];

    internal static string BoundLine(string text)
    {
        StringBuilder? bounded = null;
        for (var start = 0; start < text.Length;)
        {
            var end = text.IndexOfAny(['\r', '\n'], start);
            if (end < 0) end = text.Length;
            var next = end;
            if (next < text.Length && text[next++] == '\r' && next < text.Length && text[next] == '\n') next++;
            if (end - start > MaxLineCharacters)
            {
                bounded ??= new StringBuilder().Append(text, 0, start);
                bounded.Append(text, start, MaxLineCharacters)
                    .Append($" …[{end - start - MaxLineCharacters} chars omitted]")
                    .Append(text, end, next - end);
            }
            else
            {
                bounded?.Append(text, start, next - start);
            }
            start = next;
        }
        return bounded?.ToString() ?? text;
    }

    internal static AcceptanceRetryFailureDisposition ResolveDisposition(AcceptanceCheckResult check, string? identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return AcceptanceRetryFailureDisposition.NotRerun;

        var laneRerun = check.LaneRerun;
        if (laneRerun?.RerunFailingTestIdentities?.Contains(identity, StringComparer.Ordinal) == true ||
            check.WithinAttemptRerun?.RerunFailingTestIdentities?.Contains(identity, StringComparer.Ordinal) == true)
            return AcceptanceRetryFailureDisposition.Repeatable;

        return laneRerun?.FirstFailingTestIdentities?.Contains(identity, StringComparer.Ordinal) == true
            ? AcceptanceRetryFailureDisposition.PassedRetry
            : AcceptanceRetryFailureDisposition.NotRerun;
    }

    private static string FormatLabeledPrefix(AcceptanceRetryFailureDisposition disposition) =>
        disposition switch
        {
            AcceptanceRetryFailureDisposition.Repeatable => FailurePrefix + " [repeatable]",
            AcceptanceRetryFailureDisposition.PassedRetry => FailurePrefix + " [passed retry]",
            AcceptanceRetryFailureDisposition.NotRerun => FailurePrefix + " [not rerun]",
            _ => throw new ArgumentOutOfRangeException(nameof(disposition), disposition, null)
        };

    private static string FormatFailure(
        AcceptanceTrxFailure failure, string trxPath, int maxCharacters, string? labeledPrefix = null)
    {
        var message = string.IsNullOrEmpty(failure.Message) ? "failure message unavailable" : failure.Message;
        var stackTrace = string.IsNullOrEmpty(failure.StackTrace) ? "stack trace unavailable" : failure.StackTrace;
        var entry = BoundLine($"{labeledPrefix ?? FailurePrefix} {failure.TestName} ({failure.Outcome}){Environment.NewLine}{message}{Environment.NewLine}{stackTrace}");
        if (entry.Length <= maxCharacters) return entry;

        var retained = maxCharacters;
        while (true)
        {
            var suffix = $"{Environment.NewLine}[truncated: {entry.Length - retained} chars omitted; full failure in {trxPath}]";
            var available = Math.Max(0, maxCharacters - suffix.Length);
            // The check's receipt line already gives the path when this entry has too little room.
            if (labeledPrefix is not null && available < labeledPrefix.Length)
                return entry[..maxCharacters];
            if (available == retained) return entry[..retained] + suffix;
            retained = available;
        }
    }

    internal void AppendFailures(
        List<string> evidence, string criterionName, string trxPath, int receiptLineIndex,
        IReadOnlyList<(AcceptanceTrxFailure Failure, AcceptanceRetryFailureDisposition Disposition)> failures,
        ref int remainingEvidenceEntries)
    {
        var omittedFailures = 0;
        var duplicatePaths = new List<string>();
        foreach (var (failure, disposition) in failures)
        {
            if (string.IsNullOrWhiteSpace(failure.TestName))
            {
                evidence.Add(
                    $"detail unavailable: test identity unavailable for outcome \"{failure.Outcome}\" in partition \"{criterionName}\" at {trxPath}");
                continue;
            }

            var message = failure.Message ?? string.Empty;
            var firstLineEnd = message.IndexOfAny(['\r', '\n']);
            var key = (failure.TestName, firstLineEnd < 0 ? message : message[..firstLineEnd]);
            if (_seen.TryGetValue(key, out var earlierPath))
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(earlierPath, trxPath) &&
                    !duplicatePaths.Contains(earlierPath, StringComparer.OrdinalIgnoreCase))
                    duplicatePaths.Add(earlierPath);
                continue;
            }

            if (remainingEvidenceEntries == 0)
            {
                omittedFailures++;
                continue;
            }

            _seen.Add(key, trxPath);
            _failures.Add((evidence.Count, trxPath, failure, FormatLabeledPrefix(disposition)));
            evidence.Add(FormatFailure(failure, trxPath, MaxFailureCharacters));
            remainingEvidenceEntries--;
        }

        if (duplicatePaths.Count > 0)
            evidence[receiptLineIndex] += $" (same failures as {string.Join(", ", duplicatePaths)})";
        if (omittedFailures > 0)
            evidence.Add($"{omittedFailures} more failures omitted — see {trxPath}.");
    }

    internal static bool FitsOutput(ICollection<string> evidence, string entry, int remainingOutputEntries)
    {
        var summary = remainingOutputEntries == 0 ? string.Empty :
            $"{remainingOutputEntries} more acceptance evidence entries omitted.";
        return evidence.Sum(line => (long)line.Length) + entry.Length + summary.Length <= MaxEvidenceCharacters;
    }

    internal List<string> Complete(List<string> evidence)
    {
        var characters = evidence.Sum(entry => (long)entry.Length);
        var retained = _failures.Count;
        var summary = string.Empty;
        // Include all later receipts/diagnostics and the exact omission summary before admitting a prefix.
        while (retained > 0 && characters + summary.Length > MaxEvidenceCharacters)
        {
            retained--;
            characters -= evidence[_failures[retained].Index].Length;
            var paths = _failures.Skip(retained).Select(failure => failure.TrxPath)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            summary = $"{_failures.Count - retained} more failures omitted — see {string.Join(", ", paths)}";
        }

        for (var index = _failures.Count - 1; index >= retained; index--)
            evidence.RemoveAt(_failures[index].Index);
        if (summary.Length > 0) evidence.Add(summary);

        // Use unlabeled lengths for both output admission and the retained failure prefix.
        // Reserve complete labels even for failures shorter than their labeled prefix.
        var retainedFailures = _failures.Take(retained);
        var prefixOverhead = retainedFailures.Sum(failure =>
            Math.Max(0, failure.LabeledPrefix.Length - evidence[failure.Index].Length));
        var spareCharacters = MaxEvidenceCharacters - characters - summary.Length - prefixOverhead;
        foreach (var (index, trxPath, failure, labeledPrefix) in retainedFailures)
        {
            var reservedLength = Math.Max(evidence[index].Length, labeledPrefix.Length);
            var limit = (int)Math.Clamp(reservedLength + spareCharacters, labeledPrefix.Length, MaxFailureCharacters);
            evidence[index] = FormatFailure(failure, trxPath, limit, labeledPrefix);
            spareCharacters += reservedLength - evidence[index].Length;
        }
        return evidence;
    }
}
