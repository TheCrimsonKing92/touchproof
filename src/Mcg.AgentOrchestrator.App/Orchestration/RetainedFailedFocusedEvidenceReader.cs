using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Reads execution-owner attestations only. Missing legacy attestations grant no reuse authority.
internal static class RetainedFailedFocusedEvidenceReader
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static (ConductorParallelAcceptanceCandidate Candidate, ConductorFocusedEvidenceRequestContext Context)
        CreatePreReviewRequest(Goal goal, PreReviewEvidenceContext context, ConductorAutonomyPolicy policy,
            Func<Goal, AcceptanceGateEngineSettings> getSettings)
    {
        string? basis;
        try
        {
            basis = ConductorDriver.BuildFindingEvidenceExecutionBasisIdentity(getSettings(goal));
        }
        catch (Exception)
        {
            // Unavailable settings cannot establish reuse authority. Execution retains its failure path.
            basis = null;
        }
        const string fingerprint = PreReviewEvidenceReceipts.FindingRoundFingerprint;
        return (ConductorParallelAcceptanceCandidate.Create(goal, 0, [], context.CandidateSha, null),
            new(fingerprint, ConductorDriver.CreateFindingEvidenceBatchId(
                context.CandidateSha!, fingerprint, policy.Name, context.FocusedRequest!), [], ExecutionBasisIdentity: basis));
    }

    internal static bool HasRecordedRedReceipt(ConductorParallelAcceptanceAttempt attempt,
        PreReviewEvidenceReceipt? receipt, string goalId, PreReviewEvidenceContext context) =>
        attempt.ReconciledAt is not null && receipt is { Disposition: PreReviewEvidenceDisposition.Red } &&
        receipt.MatchesCurrentCandidate(goalId, context.CandidateSha!, context.SelectedFocusedTests);

    internal static ConductorParallelAcceptanceAttemptDecision? TryRead(
        ConductorParallelAcceptanceAttempt attempt,
        ConductorParallelAcceptanceCandidate candidate,
        string dispatchKind,
        string? request,
        ConductorFocusedEvidenceRequestContext? context)
    {
        if (attempt.Kind != ConductorParallelAcceptanceAttemptCoordinator.PreReviewEvidenceDispatchKind ||
            dispatchKind != attempt.Kind || attempt.ReconciledAt is null || attempt.CompletedAt is null ||
            attempt.Outcome != ConductorParallelAcceptanceAttemptOutcome.Failed ||
            attempt.SupersededBy is not null || attempt.RegateRequestedAt is not null || attempt.InvalidatedAt is not null ||
            context is null || string.IsNullOrWhiteSpace(attempt.FocusedExecutionBasisIdentity) ||
            string.IsNullOrWhiteSpace(context.ExecutionBasisIdentity) ||
            !string.Equals(attempt.FocusedExecutionBasisIdentity, context.ExecutionBasisIdentity, StringComparison.Ordinal) ||
            !string.Equals(attempt.CandidateKey, candidate.CandidateKey, StringComparison.Ordinal) ||
            !string.Equals(attempt.FocusedEvidenceRequest, request, StringComparison.Ordinal) ||
            attempt.FocusedEvidenceNegativeControl != context.NegativeControl ||
            !FindingEvidenceRevertPaths.SamePaths(attempt.FocusedEvidenceRevertPaths, context.RevertPaths) ||
            !FindingEvidenceMutation.Same(attempt.FocusedEvidenceMutation, context.Mutation) ||
            !string.Equals(attempt.FocusedEvidenceRepository, context.RepositoryTarget?.Key, StringComparison.Ordinal) ||
            !string.Equals(attempt.FindingRoundFingerprint, context.FindingRoundFingerprint, StringComparison.Ordinal) ||
            !string.Equals(attempt.FocusedEvidenceBatchId, context.BatchId, StringComparison.Ordinal) ||
            attempt.FocusedEvidenceRunsBaselineArm != context.RunBaselineArm ||
            attempt.OriginalFocusedReceipt is not { Accepted: true, Passed: false } receipt ||
            !string.Equals(receipt.CandidateSha, attempt.BranchHeadSha, StringComparison.Ordinal) ||
            !string.Equals(receipt.ExecutionBasisIdentity, context.ExecutionBasisIdentity, StringComparison.Ordinal) ||
            !string.Equals(receipt.FindingRoundFingerprint, context.FindingRoundFingerprint, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(attempt.ReplayResultFingerprint)) return null;

        try
        {
            var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(
                File.ReadAllText(attempt.ResultPath), JsonOptions);
            if (artifact is not { Kind: "focused-evidence", FocusedEvidence: { Accepted: true, Passed: false } evidence } ||
                !string.Equals(artifact.BranchHeadSha, attempt.BranchHeadSha, StringComparison.Ordinal) ||
                !string.Equals(evidence.Request, request, StringComparison.Ordinal) ||
                Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(artifact, JsonOptions)))) !=
                    attempt.ReplayResultFingerprint ||
                PreReviewEvidenceRelaunchProvenance.Classify(attempt, evidence, terminalWithoutRun: false) !=
                    PreReviewEvidencePriorResultClass.TestFailure ||
                evidence.Checks.Any(check => !check.Passed && ConductorDriver.IsBlockingTimeoutCheck(check.Name)) ||
                evidence.RevertPathsRejection is not null ||
                (evidence.Arms ?? []).Any(arm => arm is null || !arm.Accepted || arm.RevertPathsRejection is not null || arm.Disposition is
                    FindingEvidenceArmDisposition.ApparatusFailure or FindingEvidenceArmDisposition.Inconclusive) ||
                (receipt.Arms ?? []).Any(arm => arm is null || !arm.Accepted || arm.Disposition is
                    FindingEvidenceArmDisposition.ApparatusFailure or FindingEvidenceArmDisposition.Inconclusive)) return null;

            var arm = receipt.Arms?.SingleOrDefault(value => value.Arm == FindingEvidenceArm.Candidate);
            if (arm is not { Accepted: true, Passed: false, Disposition: FindingEvidenceArmDisposition.Red,
                    ExecutedTestCount: > 0, FailingTestIdentities.Count: > 0, TestResultPaths.Count: > 0 } ||
                evidence.Checks.Count == 0 || evidence.Checks.Any(check => check.ExecutedTestCount is null) ||
                evidence.Checks.Sum(check => check.ExecutedTestCount!.Value) != arm.ExecutedTestCount ||
                !arm.FailingTestIdentities.ToHashSet(StringComparer.Ordinal)
                    .SetEquals(ConductorDriver.ExtractFailingTestIdentities(evidence.Checks)) ||
                !SamePaths(arm.TestResultPaths, evidence.Checks.SelectMany(check => check.TestResultPaths ?? [])) ||
                !SamePaths(attempt.TestResultPaths ?? [], arm.TestResultPaths))
                return null;

            var selections = (request ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .SelectMany(ConductorDriver.ParseDispositionSelections).ToArray();
            var classes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var selection in FindingEvidenceRoundPlanner.ExpandSelections(selections))
            {
                if (!FindingEvidenceFilterSyntax.TryGetExpandableClass(selection.TestClass, out var testClass, out _)) return null;
                classes.Add(testClass);
            }
            if (!HasRetainedRedCoverage(arm, receipt.Arms!, classes)) return null;

            return ConductorParallelAcceptanceAttemptDecision.Completed(attempt,
                ConductorParallelAcceptanceRunResult.Focused(candidate, evidence));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or
                                   InvalidDataException or ArgumentException or InvalidOperationException or
                                   NotSupportedException or OverflowException or System.Xml.XmlException)
        {
            return null;
        }
    }

    private static bool SamePaths(IEnumerable<string> left, IEnumerable<string> right) =>
        left.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase)
            .SetEquals(right.Select(Path.GetFullPath));

    private static bool HasRetainedRedCoverage(FindingEvidenceArmReceipt arm,
        IReadOnlyList<FindingEvidenceArmReceipt> arms, IReadOnlyCollection<string> classes)
    {
        // Keep retained files read-locked while the existing coverage and identity readers inspect them.
        var locks = new List<FileStream>();
        try
        {
            foreach (var retainedArm in arms)
            {
                if (retainedArm.TestResultPaths is not { Count: > 0 } paths ||
                    retainedArm.ReceiptArtifacts is not { Count: > 0 } artifacts) return false;
                var trx = artifacts.Where(value => value.Kind == "trx").ToArray();
                if (trx.Length != paths.Count || !SamePaths(paths, trx.Select(value => value.Path))) return false;
                foreach (var artifact in trx)
                {
                    var stream = File.Open(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                    locks.Add(stream);
                    if (artifact.Length <= 0 || stream.Length != artifact.Length ||
                        Convert.ToHexStringLower(SHA256.HashData(stream)) != artifact.Sha256) return false;
                }
            }
            if (!FindingEvidenceTrxCoverage.HasContentBoundRedTrxCoverage(arm, classes)) return false;
            var failures = arm.TestResultPaths!.Select(AcceptanceTrxFailureReader.Read).ToArray();
            return failures.All(result => result.Status == AcceptanceTrxReadStatus.Readable) &&
                arm.FailingTestIdentities!.ToHashSet(StringComparer.Ordinal).SetEquals(failures
                    .SelectMany(result => result.Failures)
                    .Where(failure => failure.Outcome.Equals("Failed", StringComparison.OrdinalIgnoreCase))
                    .Select(failure => failure.TestName));
        }
        finally
        {
            foreach (var stream in locks) stream.Dispose();
        }
    }
}
