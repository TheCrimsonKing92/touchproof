using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class AcceptancePreReviewEvidenceCoverage
{
    private static readonly Regex ClassClause = new(
        @"\AFullyQualifiedName\s*~\s*(?<class>[A-Za-z_][A-Za-z0-9_]*)\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    internal sealed record MatchResult(
        IReadOnlyList<string> AttemptIds,
        IReadOnlyList<string> EvidencePointers,
        IReadOnlyList<string> TestResultPaths);

    internal static MatchResult? Match(
        AcceptanceManifestCheck gateCheck,
        string goalId,
        string candidateTreeSha,
        string manifestIdentity,
        IReadOnlyList<PreReviewEvidenceReceipt> receipts)
    {
        if (string.IsNullOrWhiteSpace(candidateTreeSha) || string.IsNullOrWhiteSpace(manifestIdentity) ||
            !GoalAcceptanceVerifier.TryGetInfrastructurePartitionId(gateCheck, out _, out var filter) ||
            gateCheck.Arguments.Count(argument => argument.Equals("--filter", StringComparison.OrdinalIgnoreCase) ||
                argument.StartsWith("--filter=", StringComparison.OrdinalIgnoreCase)) != 1 ||
            ReadClassUnion(filter) is not { } gateClasses)
            return null;

        var projectLabel = GoalAcceptanceVerifier.ProjectLabel(gateCheck.Project!);
        var checkPrefix = $"reviewer focused evidence: {projectLabel} ";
        var selectionPrefix = $"{projectLabel}: ";
        var uncovered = new HashSet<string>(gateClasses, StringComparer.Ordinal);
        var attempts = new HashSet<string>(StringComparer.Ordinal);
        var pointers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newerIneligible = new List<(PreReviewEvidenceReceipt Receipt, HashSet<string> Classes)>();

        // A newer execution that reports unusable results for a class supersedes older evidence
        // on the same candidate. Synthetic receipts describe no execution and supersede nothing.
        foreach (var receipt in receipts.Reverse().OrderByDescending(receipt => receipt.RecordedAt))
        {
            if (!string.Equals(receipt.GoalId, goalId, StringComparison.OrdinalIgnoreCase) || receipt.IsSyntheticReuse)
                continue;

            if (!IsEligible(receipt, candidateTreeSha, manifestIdentity))
            {
                var namedClasses = receipt.Checks.SelectMany(check => ReadNamedClasses(check.Name, checkPrefix) ?? [])
                    .Concat(receipt.SelectedFocusedTests.SelectMany(selection => ReadNamedClasses(selection, selectionPrefix) ?? []))
                    .ToHashSet(StringComparer.Ordinal);
                if (namedClasses.Overlaps(gateClasses))
                    newerIneligible.Add((receipt, namedClasses));
                continue;
            }

            foreach (var check in receipt.Checks)
            {
                if (ReadNamedClasses(check.Name, checkPrefix) is not { } classes ||
                    !classes.All(gateClasses.Contains) || !classes.Any(uncovered.Contains) ||
                    newerIneligible.Any(newer => SameCandidate(newer.Receipt, receipt) && classes.Any(newer.Classes.Contains)) ||
                    check.TestResultPaths is not { Count: > 0 } testPaths || testPaths.Any(string.IsNullOrWhiteSpace))
                    continue;

                var sourceAttempts = string.IsNullOrWhiteSpace(receipt.SourceAttemptId)
                    ? testPaths.Select(ReadAttemptId).ToArray() : [receipt.SourceAttemptId];
                if (sourceAttempts.Any(attempt => attempt is null))
                    continue;

                uncovered.ExceptWith(classes);
                attempts.UnionWith(sourceAttempts.OfType<string>());
                pointers.Add(receipt.EvidencePointer!);
                paths.UnionWith(testPaths);
                if (uncovered.Count == 0)
                    return new MatchResult([.. attempts], [.. pointers], [.. paths]);
            }
        }

        return null;
    }

    private static bool IsEligible(PreReviewEvidenceReceipt receipt, string candidateTreeSha, string manifestIdentity) =>
        receipt.Disposition == PreReviewEvidenceDisposition.Green &&
        receipt.FailedCheckCount == 0 && receipt.FailingTestIdentities.Count == 0 &&
        receipt.EvidenceTimeoutChecks is null or { Count: 0 } &&
        receipt.SelectedFocusedTests.Count > 0 && receipt.Checks.Count > 0 &&
        receipt.PassedCheckCount == receipt.Checks.Count && receipt.Checks.All(check => check.Passed && check.ExitCode == 0) &&
        !string.IsNullOrWhiteSpace(receipt.EvidencePointer) &&
        SameTree(receipt.CandidateTreeSha, candidateTreeSha) &&
        string.Equals(receipt.ManifestIdentity, manifestIdentity, StringComparison.Ordinal);

    private static bool SameCandidate(PreReviewEvidenceReceipt left, PreReviewEvidenceReceipt right) =>
        string.Equals(left.CandidateSha, right.CandidateSha, StringComparison.OrdinalIgnoreCase) ||
        SameTree(left.CandidateTreeSha, right.CandidateTreeSha);

    private static bool SameTree(string? left, string? right) =>
        !string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right) &&
        string.Equals(left.Trim(), right.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string[]? ReadNamedClasses(string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? ReadClassUnion(value[prefix.Length..]) : null;

    private static string[]? ReadClassUnion(string filter)
    {
        var clauses = filter.Split('|', StringSplitOptions.TrimEntries);
        for (var index = 0; index < clauses.Length; index++)
        {
            var match = ClassClause.Match(clauses[index]);
            if (!match.Success)
                return null;

            clauses[index] = match.Groups["class"].Value;
        }

        return clauses;
    }

    private static string? ReadAttemptId(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        if (!Path.GetExtension(path).Equals(".trx", StringComparison.OrdinalIgnoreCase))
            return null;

        var separator = fileName.IndexOf('.');
        return separator > 0 && separator < fileName.Length - 1 ? fileName[..separator] : null;
    }
}
