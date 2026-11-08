using System.Security.Cryptography;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Owns result-path normalization and content-bound artifact capture for finding-evidence receipts.
internal static class FindingEvidenceArmReceiptFactory
{
    internal static FindingEvidenceArmReceipt Create(FocusedEvidenceArmRunResult arm)
    {
        var testResultPaths = NormalizeFindingEvidenceTestResultPaths(
            arm.Checks.SelectMany(check => check.TestResultPaths ?? []));
        var normalizedPaths = NormalizeFindingEvidenceTestResultPaths(arm.Checks
            .Where(check => check.TestResultPaths is not { Count: > 0 })
            .SelectMany(check => check.NormalizedTestResultPaths ?? []));
        var receiptArtifacts = CaptureFindingEvidenceArtifacts(testResultPaths, "trx")
            .Concat(CaptureFindingEvidenceArtifacts(normalizedPaths, AcceptanceCohortCheckEvidence.NormalizedFormat))
            .ToArray();
        return new FindingEvidenceArmReceipt(
            arm.Arm,
            arm.Sha,
            arm.Disposition,
            arm.Accepted,
            arm.Passed,
            arm.Summary,
            testResultPaths
                .Concat(normalizedPaths)
                .Concat(arm.Checks.Select(check => check.ArtifactsPath ?? string.Empty))
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            arm.Checks
                .SelectMany(check => check.FailingTestIdentities ?? [])
                .Distinct(StringComparer.Ordinal)
                .ToArray(),
            TryGetExecutedTestCount(arm.Checks),
            testResultPaths,
            receiptArtifacts, arm.RestoredPaths, arm.DroppedPaths, arm.NotRunSelections,
            normalizedPaths.Count > 0 ? normalizedPaths : null);
    }

    private static IReadOnlyList<string> NormalizeFindingEvidenceTestResultPaths(IEnumerable<string> paths)
    {
        try
        {
            var materialized = paths.Where(path => !string.IsNullOrWhiteSpace(path)).ToArray();
            if (materialized.Any(path => !Path.IsPathFullyQualified(path)))
            {
                return [];
            }

            return materialized
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return [];
        }
    }

    private static IReadOnlyList<AcceptanceCohortEvidenceArtifact> CaptureFindingEvidenceArtifacts(
        IReadOnlyList<string> testResultPaths, string kind)
    {
        var artifacts = new List<AcceptanceCohortEvidenceArtifact>();
        try
        {
            foreach (var path in testResultPaths)
            {
                using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                artifacts.Add(new AcceptanceCohortEvidenceArtifact(
                    kind,
                    path,
                    Convert.ToHexStringLower(SHA256.HashData(stream)),
                    stream.Length));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return [];
        }

        return artifacts;
    }

    private static int? TryGetExecutedTestCount(IReadOnlyList<AcceptanceCheckResult> checks) =>
        checks.Count > 0 && checks.All(check => check.ExecutedTestCount.HasValue)
            ? checks.Sum(check => check.ExecutedTestCount!.Value)
            : null;
}
