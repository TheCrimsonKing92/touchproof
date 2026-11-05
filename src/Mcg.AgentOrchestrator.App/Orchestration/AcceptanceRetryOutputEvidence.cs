using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AcceptanceRetryOutputEvidence
{
    private static readonly Regex AcceptanceRetryEvidencePattern = new(
        @"error CS\d+|error MSB\d+|\[FAIL\]",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    internal static IEnumerable<string> ExtractConcreteOutputEvidence(AcceptanceCheckResult criterion)
    {
        foreach (var line in SplitEvidenceLines(criterion.OutputTail))
        {
            if (AcceptanceRetryEvidencePattern.IsMatch(line))
            {
                yield return line;
            }
        }
    }

    internal static string FormatPreReviewBuildDiagnostic(IReadOnlyList<AcceptanceCheckResult> checks)
    {
        var failedChecks = checks.Where(check => !check.Passed).ToArray();
        var lines = failedChecks
            .Where(check => !string.IsNullOrWhiteSpace(check.OutputTail))
            .SelectMany(ExtractConcreteOutputEvidence)
            .Take(5)
            .ToArray();
        lines = lines.Length > 0 ? lines : failedChecks
            .Select(check => check.ResultSummary)
            .Concat(failedChecks.Select(check => check.OutputTail))
            .Where(text => !string.IsNullOrWhiteSpace(text))
            .SelectMany(text => SplitEvidenceLines(text!))
            .Take(5)
            .ToArray();
        var diagnostic = lines.Length == 0 ? "no diagnostic output was captured" : string.Join(" | ", lines);
        return diagnostic.Length <= 1000 ? diagnostic : $"{diagnostic[..997]}...";
    }

    private static IEnumerable<string> SplitEvidenceLines(string? text) =>
        (text ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.TrimEnd())
            .Where(line => !string.IsNullOrWhiteSpace(line));

    internal static void AppendBounded(
        ICollection<string> evidence,
        IReadOnlyList<string> outputEvidence,
        ref int remainingEvidenceEntries)
    {
        var entryLimit = Math.Min(outputEvidence.Count, remainingEvidenceEntries);
        var retainedCount = 0;
        for (; retainedCount < entryLimit; retainedCount++)
        {
            var entry = AcceptanceRetryEvidenceBudget.BoundLine(outputEvidence[retainedCount]);
            if (!AcceptanceRetryEvidenceBudget.FitsOutput(evidence, entry, outputEvidence.Count - retainedCount - 1))
                break;
            evidence.Add(entry);
        }

        remainingEvidenceEntries -= retainedCount;
        if (retainedCount < outputEvidence.Count)
        {
            evidence.Add($"{outputEvidence.Count - retainedCount} more acceptance evidence entries omitted.");
        }
    }
}
