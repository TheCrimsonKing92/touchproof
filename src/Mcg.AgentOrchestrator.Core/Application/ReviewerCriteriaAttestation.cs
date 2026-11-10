namespace Mcg.AgentOrchestrator.Core;

// Owns criterion-attestation validation and candidate binding of externally owned
// evidence. The kernel still adjudicates source findings and records task failure.
internal static class ReviewerCriteriaAttestation
{
    internal static string? Evaluate(Goal goal, TaskSpec task, TaskVerificationRecord verification,
        IClock clock, Action<Goal, TaskId?, ProgressKind, string> append,
        out string? nonPassingCriteriaDiagnostic)
    {
        nonPassingCriteriaDiagnostic = null;
        if (verification.WorkerResultPresent &&
            task.RequiredRole == AgentRole.Reviewer &&
            goal.RefinedSpec is { AcceptanceCriteria.Count: > 0 } refinedSpec)
        {
            if (!WorkerResultBlockers.TryFindCriteriaVerdicts(
                    verification,
                    out var criterionVerdicts,
                    out var criteriaDiagnostic))
            {
                return $"Reviewer WORKER_RESULT criteria attestation invalid: {criteriaDiagnostic}";
            }

            var registeredVerdicts = criterionVerdicts
                .Where(item => item.CriterionIndex < refinedSpec.AcceptanceCriteria.Count)
                .ToArray();
            var actualIndices = registeredVerdicts
                .Select(item => item.CriterionIndex)
                .OrderBy(index => index)
                .ToArray();
            var expectedIndices = Enumerable.Range(0, refinedSpec.AcceptanceCriteria.Count).ToArray();
            if (!actualIndices.SequenceEqual(expectedIndices))
            {
                var received = string.Join(", ", actualIndices);
                var missing = string.Join(", ", expectedIndices.Except(actualIndices).Select(index => index + 1));
                var duplicated = string.Join(", ", actualIndices
                    .GroupBy(index => index)
                    .Where(group => group.Count() > 1)
                    .Select(group => group.Key + 1));
                return "Reviewer WORKER_RESULT criteria attestation invalid: " +
                    $"expected every registered criterion_index 0..{expectedIndices.Length - 1} (criteria 1 to {expectedIndices.Length}) exactly once; " +
                    $"received registered indices {(received.Length == 0 ? "none" : received)}; " +
                    $"missing criteria: {(missing.Length == 0 ? "none" : missing)}; " +
                    $"duplicated criteria: {(duplicated.Length == 0 ? "none" : duplicated)}.";
            }

            var nonPassingVerdicts = registeredVerdicts
                .Where(item =>
                    !item.Verdict.Equals("met", StringComparison.Ordinal) &&
                    !goal.EffectiveAcceptanceCriteriaCorrections.Any(correction =>
                        correction.IsWaiver &&
                        string.Equals(
                            correction.SupersededCriterion,
                            refinedSpec.AcceptanceCriteria[item.CriterionIndex].Trim(),
                            StringComparison.OrdinalIgnoreCase)))
                .ToArray();
            var workerOwnedNonPassingVerdicts = nonPassingVerdicts
                .Where(item => !item.Verdict.Equals("not-verifiable", StringComparison.Ordinal) ||
                    VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(goal, item.CriterionIndex))
                .ToArray();
            foreach (var deferred in nonPassingVerdicts.Except(workerOwnedNonPassingVerdicts))
            {
                var matches = goal.CriterionEvidenceObligations.Where(item =>
                    item.CriterionIndex == deferred.CriterionIndex &&
                    item.CriterionVersion == goal.AuthoritativeRefinedSpecVersion!.Version &&
                    item.State != CriterionEvidenceState.Repaired).ToArray();
                if (matches.Length != 1)
                {
                    nonPassingCriteriaDiagnostic =
                        $"Reviewer WORKER_RESULT criteria attestation cannot be deferred: {VerificationFindingEvidencePolicy.FormatCriterionIndex(deferred.CriterionIndex)} has {matches.Length} authoritative obligation records; operator repair is required.";
                    continue;
                }
                var obligation = goal.BindDeferredCriterionObligation(
                    deferred.CriterionIndex,
                    goal.AuthoritativeRefinedSpecVersion.Version,
                    verification.ReviewedCommit,
                    clock.UtcNow) ?? matches[0];
                append(goal, task.Id, ProgressKind.TaskNote,
                    $"Deferred criterion evidence remains pending: obligation={obligation.DisplayLabel}; owner={obligation.Owner}; " +
                    $"state={obligation.State}; next_action={obligation.RequiredScope}; reviewer_verdict={deferred.Verdict}; " +
                    $"expected_candidate={obligation.ExpectedCandidateSha ?? "unbound"}.");
            }
            foreach (var advisory in registeredVerdicts.Where(item =>
                         item.Verdict.Equals("met", StringComparison.Ordinal) &&
                         !VerificationFindingEvidencePolicy.IsWorkerOwnedCriterionObligation(goal, item.CriterionIndex) &&
                         !goal.EffectiveAcceptanceCriteriaCorrections.Any(correction =>
                             correction.IsWaiver &&
                             string.Equals(
                                 correction.SupersededCriterion,
                                 refinedSpec.AcceptanceCriteria[item.CriterionIndex].Trim(),
                                 StringComparison.OrdinalIgnoreCase))))
            {
                var version = goal.AuthoritativeRefinedSpecVersion!.Version;
                var obligation = goal.BindDeferredCriterionObligation(
                    advisory.CriterionIndex,
                    version,
                    verification.ReviewedCommit,
                    clock.UtcNow);
                if (obligation?.Owner != CriterionEvidenceOwner.Acceptance)
                    continue;
                append(goal, task.Id, ProgressKind.TaskNote,
                    $"Acceptance-gate-owned criterion met verdict is advisory; the gate remains the evidence: " +
                    $"obligation={obligation.DisplayLabel}; state={obligation.State}; expected_candidate={obligation.ExpectedCandidateSha ?? "unbound"}.");
            }
            if (workerOwnedNonPassingVerdicts.Length > 0)
            {
                var details = string.Join(
                    "; ",
                    workerOwnedNonPassingVerdicts.Select(item =>
                        $"{VerificationFindingEvidencePolicy.FormatCriterionIndex(item.CriterionIndex)} verdict={item.Verdict} evidence={item.Evidence}"));
                var repairCommands = string.Join(
                    "; ",
                    workerOwnedNonPassingVerdicts
                        .Where(item => item.Verdict.Equals("not-verifiable", StringComparison.Ordinal))
                        .Select(item =>
                            $"criterion-evidence-map --goal {goal.Id.Value} --criterion {item.CriterionIndex + 1} " +
                            $"--version {goal.AuthoritativeRefinedSpecVersion!.Version} acceptance {CriterionEvidenceScopes.FullAcceptanceGate} " +
                            $"deferred-criterion-{item.CriterionIndex} {verification.ReviewedCommit ?? "missing"}"));
                nonPassingCriteriaDiagnostic =
                    $"Reviewer WORKER_RESULT criteria attestation rejected: non-waived criteria are not passing: {details}." +
                    (repairCommands.Length == 0 ? string.Empty : $" Operator repair: {repairCommands}.");
            }

            foreach (var extra in criterionVerdicts.Where(item =>
                         item.CriterionIndex >= refinedSpec.AcceptanceCriteria.Count))
            {
                append(
                    goal,
                    task.Id,
                    ProgressKind.TaskNote,
                    $"Reviewer informational extra criterion attestation recorded: " +
                    $"{VerificationFindingEvidencePolicy.FormatCriterionIndex(extra.CriterionIndex)}; verdict={extra.Verdict}; evidence={extra.Evidence}");
            }
        }
        return null;
    }
}
