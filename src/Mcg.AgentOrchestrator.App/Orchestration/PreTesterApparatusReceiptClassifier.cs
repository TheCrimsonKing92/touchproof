using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class PreTesterApparatusReceiptClassifier
{
    internal static bool IsBuildPhaseOnly(FocusedEvidenceRunResult? evidence)
    {
        if (evidence is null) return false;
        var checks = TimedOutSelectionRerunMapping.AllRoundChecks(evidence);
        return checks.Any(check => !check.Passed) && checks.All(check =>
            (check.Passed || check.FailureClassification == AcceptanceFailureClassifications.BuildPhaseTimeout) &&
            check.ExecutedTestCount is not > 0 && check.FailingTestIdentities is not { Count: > 0 });
    }

    internal static bool IsTransientAttemptOutcome(ConductorParallelAcceptanceAttemptOutcome? outcome) => outcome is
        ConductorParallelAcceptanceAttemptOutcome.BlockedBuildSlot or
        ConductorParallelAcceptanceAttemptOutcome.BlockedBuildLock or
        ConductorParallelAcceptanceAttemptOutcome.Cancelled or
        ConductorParallelAcceptanceAttemptOutcome.InfrastructureDeferred or
        ConductorParallelAcceptanceAttemptOutcome.StructuralCoveragePermitUnavailable or
        ConductorParallelAcceptanceAttemptOutcome.StaleCandidate;

    // Receipt validity and failure attribution determine whether a completed run is unusable.
    internal static (string Outcome, FindingEvidenceArmReceipt? Candidate, IReadOnlyList<string> FailingTests,
        PreTesterUnusableReason? Unusable)
        ClassifyOutcome(FocusedEvidenceRunResult? evidence, bool completed,
        string? candidateSha, IReadOnlyList<FindingEvidenceSelection> selections,
        Func<IReadOnlyList<string>, bool> everyFailureInsideCandidateChanges)
    {
        var candidateArm = (evidence?.Arms ?? [])
            .FirstOrDefault(arm => arm.Arm == FindingEvidenceArm.Candidate &&
                                   string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase));
        var candidate = candidateArm is null ? null : ConductorDriver.CreateFindingEvidenceArmReceipt(candidateArm);
        if (completed && IsAssemblyCleanupOnly(candidateArm))
            return ("unusable", candidate, [], new(AcceptanceFailureClassifications.AssemblyCleanupFailure, []));
        var baseline = (evidence?.Arms ?? []).Select(ConductorDriver.CreateFindingEvidenceArmReceipt)
            .FirstOrDefault(arm => arm.Arm == FindingEvidenceArm.Baseline);
        var failingTests = candidate?.FailingTestIdentities ?? [];
        var candidateRed = completed && evidence!.Accepted &&
                           candidate is { Accepted: true, Disposition: FindingEvidenceArmDisposition.Red };
        var unlistedFailures = failingTests.Where(test => !(PreTesterAlwaysRunGuardTestClasses.IsListed(test) || PreTesterTestSourceGuardClasses.IsCovered(test))).ToArray();
        var actionableRed = candidateRed && failingTests.Count > 0 &&
                            (baseline is null
                                ? unlistedFailures.Length == 0 || everyFailureInsideCandidateChanges(unlistedFailures)
                                : baseline.Disposition == FindingEvidenceArmDisposition.Green);
        var failure = FirstGreenFailure(evidence, completed, candidate, selections);
        var outcome = failure is null ? "green" : actionableRed ? "actionable-red" : candidateRed ? "red" : "unusable";
        return (outcome, candidate, failingTests, outcome == "unusable" ? failure : null);
    }

    private static bool IsAssemblyCleanupOnly(FocusedEvidenceArmRunResult? candidate)
    {
        var failures = candidate?.Checks.Where(check => !check.Passed).ToArray() ?? [];
        return failures.Length > 0 && failures.All(check =>
            check.FailureClassification == AcceptanceFailureClassifications.AssemblyCleanupFailure);
    }

    private static PreTesterUnusableReason? FirstGreenFailure(FocusedEvidenceRunResult? evidence,
        bool completed, FindingEvidenceArmReceipt? candidate, IReadOnlyList<FindingEvidenceSelection> selections)
    {
        if (!completed) return new("not-completed", []);
        if (evidence is not { IsValidEvidence: true }) return new("evidence-invalid",
            evidence is null ? [] : TimedOutSelectionRerunMapping.AllRoundChecks(evidence)
                .Where(check => !check.Passed && check.FailureClassification ==
                    AcceptanceFailureClassifications.FocusedSelectionApparatusFailure)
                .SelectMany(check => FocusedEvidenceAllSkippedSelectionDecision.ReadZeroMatchSelections(check.OutputTail))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
        if (candidate is null) return new("candidate-arm-missing", []);
        if (!candidate.Accepted) return new("candidate-arm-not-accepted", []);
        if (!candidate.Passed) return new("candidate-arm-not-passed", []);
        if (candidate.Disposition != FindingEvidenceArmDisposition.Green) return new("candidate-arm-not-green", []);
        if (candidate.ExecutedTestCount is not > 0) return new("executed-count-missing", []);
        return ContentBoundFailure(candidate,
            selections.Select(selection => selection.TestClass)
                .Except((evidence.Coverage?.DroppedSelections ?? []).Select(drop => drop.Selection), StringComparer.OrdinalIgnoreCase)
                .ToHashSet(StringComparer.OrdinalIgnoreCase),
            (evidence.Arms ?? []).Where(arm => arm.Arm == FindingEvidenceArm.Candidate &&
                string.Equals(arm.Sha, candidate.Sha, StringComparison.OrdinalIgnoreCase))
                .SelectMany(arm => arm.Checks).Select(check => check.Name).ToArray());
    }

    internal static PreTesterUnusableReason? ContentBoundFailure(FindingEvidenceArmReceipt candidate,
        IReadOnlyCollection<string> requiredSelections, IReadOnlyList<string>? checkNames = null)
    {
        var failure = candidate.TestResultPaths is { Count: > 0 } ||
                      candidate.NormalizedTestResultPaths is not { Count: > 0 }
            ? ContentBoundGreenTrxEvidenceCheck.FirstFailure(
                candidate.TestResultPaths, candidate.ReceiptArtifacts, candidate.ExecutedTestCount ?? 0,
                requiredSelections, allowShortNamePrefixCoverage: true)
            : ContentBoundGreenNormalizedEvidenceCheck.FirstFailure(
                candidate.NormalizedTestResultPaths, candidate.ReceiptArtifacts, candidate.ExecutedTestCount ?? 0,
                requiredSelections, checkNames is { Count: > 0 } && checkNames.All(CrosswalkFocusedEvidence.IsEvidenceCheckName)
                    ? (selection, ids) => CrosswalkSelectorResolution.Resolve([selection], ids).Selected
                    : null);
        return failure is null ? null : new(failure.Check, failure.MissingClasses);
    }

    internal static bool IsApparatusUnusable(FocusedEvidenceRunResult? evidence)
    {
        if (evidence is null) return false;
        if (TimedOutSelectionRerunMapping.AllRoundChecks(evidence).Any(check =>
                !check.Passed && AcceptanceFailureClassifications.IsEnvironmentalApparatus(check.FailureClassification)))
            return true;
        var candidate = (evidence.Arms ?? []).Where(arm => arm.Arm == FindingEvidenceArm.Candidate)
            .Select(ConductorDriver.CreateFindingEvidenceArmReceipt).FirstOrDefault();
        return candidate is { Disposition: not FindingEvidenceArmDisposition.Green,
            ExecutedTestCount: null or 0, FailingTestIdentities.Count: 0 };
    }
}
