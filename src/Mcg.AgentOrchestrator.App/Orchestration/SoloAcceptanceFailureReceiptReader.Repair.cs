using System.Text.Json;
using System.Xml;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SoloRepairPartition(
    IReadOnlyList<SoloRepairFinding> Findings,
    AttributedFailure? Introduced);

internal static partial class SoloAcceptanceFailureReceiptReader
{
    internal static SoloRepairPartition? ReadRepairPartition(Goal goal, SoloAcceptanceConsumptionContext? current,
        AcceptanceVerificationSummary? completion = null)
    {
        var summary = goal.LatestAcceptanceFailure;
        if (summary?.SoloFailureReceipt is not { } retained) return null;
        try
        {
            var (receipt, proof, reason) = ReadProof(goal, current, retainUnresolvedIdentities: true);
            if (receipt is null || proof is null)
                return UnresolvedChecks(summary, retained, completion, reason);

            var checks = proof.Checks.Select(c => c.Check).ToArray();
            var introducedOnly = checks.All(check =>
                    (check.FailingTestIdentities is { Count: > 0 } identities &&
                     check.FailingTestAttributions is { Count: > 0 } origins &&
                     origins.All(origin => origin.Origin == AcceptanceTestFailureOrigin.Introduced) &&
                     SameNames(identities, origins.Select(origin => origin.TestIdentity))) ||
                    SoloCoveredCheckInheritance.CoveredChecks(check, checks) is not null);
            // Keep the strict introduced-only consumer, including its confirmed-flake semantics.
            var strict = introducedOnly ? Read(goal, current) : null;

            var findings = new List<SoloRepairFinding>();
            var baselineRows = proof.BaselineChecks.ToDictionary(item => item.Check.Name,
                item => new Lazy<IReadOnlyList<TrxRow>>(() =>
                    ReadRows(item, receipt.ResultsRoot, partitionAmbiguousRows: true).ValidateRows()),
                StringComparer.Ordinal);
            foreach (var item in proof.Checks)
            {
                var check = item.Check;
                if (SoloCoveredCheckInheritance.CoveredChecks(check, checks) is not null) continue;
                var candidateRows = new Lazy<ILookup<string, TrxRow>>(() =>
                    ReadRows(item, receipt.ResultsRoot, partitionAmbiguousRows: true)
                    .ValidateRows().SelectMany(row => (row.AmbiguousIdentities ?? [row.Key])
                        .Select(key => (Key: key, Row: row))).ToLookup(item => item.Key, item => item.Row, StringComparer.Ordinal));
                var identities = (check.FailingTestIdentities ?? []).DefaultIfEmpty("").Distinct(StringComparer.Ordinal);
                foreach (var identity in identities)
                {
                    var finding = BindRepairSelection(NewUnresolvedFinding(receipt.CandidateSha, receipt.MainSha,
                        receipt.BaselineSha, receipt.AttemptId, receipt.Evidence, check.Name, identity, check.TestProjectPath,
                        SoloHoldReasons.BaselineInheritedOrUncovered), check);
                    try
                    {
                        if (strict?.Failure is not null)
                        {
                            if (strict.IntroducedRowFeedback?.TryGetValue((check.Name, identity), out var feedback) != true) continue;
                            var binding = proof.Bindings.Single(b => b.CheckName == check.Name && b.TestIdentity == identity);
                            var baseline = proof.BaselineChecks.Single(b => b.Check.Name == binding.BaselineCheckName);
                            findings.Add(finding with { Reason = null, Feedback = feedback, BaselineCheck = binding.BaselineCheckName,
                                CandidateArtifacts = item.Artifacts, BaselineArtifacts = baseline.Artifacts });
                        }
                        else
                            findings.Add(strict is null
                                ? ReadRepairFinding(item, identity, finding, receipt, proof, reason, candidateRows, baselineRows)
                                : finding with { Reason = strict.Reason });
                    }
                    catch (Exception ex) when (IsRepairEvidenceError(ex))
                    {
                        findings.Add(finding with { Reason = ex.Message });
                    }
                }
            }
            return new(findings, RestoreIntroduced(findings));
        }
        catch (Exception ex) when (IsRepairEvidenceError(ex))
        {
            return UnresolvedChecks(summary, retained, completion, ex.Message);
        }
    }

    private static SoloRepairPartition UnresolvedChecks(AcceptanceFailureSummary summary, SoloAcceptanceFailureReceipt receipt,
        AcceptanceVerificationSummary? completion, string reason)
    {
        var observed = completion is not null && completion.BranchHeadSha == summary.BranchHeadSha &&
            completion.MainHeadSha == summary.MainHeadSha && completion.AttemptId == summary.AttemptId
            ? completion.RequiredUnmetCriteria.ToLookup(check => check.Name, StringComparer.Ordinal) : null;
        var findings = new List<SoloRepairFinding>();
        foreach (var name in summary.FailedChecks)
        {
            var checks = observed?[name].ToArray();
            var check = checks is [var only] && Usable(only) && !only.Passed && !only.Advisory ? only : null;
            foreach (var identity in (check?.FailingTestIdentities ?? []).DefaultIfEmpty("").Distinct(StringComparer.Ordinal))
            {
                var finding = NewUnresolvedFinding(summary.BranchHeadSha ?? "", summary.MainHeadSha ?? "", "",
                    summary.AttemptId ?? "", receipt.Evidence, name, identity, check?.TestProjectPath, reason);
                findings.Add(check is null ? finding : BindRepairSelection(finding, check));
            }
        }
        return new(findings, null);
    }

    private static SoloRepairFinding NewUnresolvedFinding(string candidate, string main, string baseline, string attempt,
        SoloAcceptanceArtifact proof, string check, string identity, string? project, string reason) =>
        new(candidate, main, baseline, check, identity, project, null, null, false, attempt, proof, [], [], reason);

    private static SoloRepairFinding BindRepairSelection(SoloRepairFinding finding, AcceptanceCheckResult check)
    {
        if (string.IsNullOrWhiteSpace(finding.Identity) || finding.Identity != finding.Identity.Trim() ||
            string.IsNullOrWhiteSpace(finding.Project)) return finding;
        var selector = AcceptanceFailureAttributionPlanner.NormalizeIdentity(finding.Identity);
        var selection = ConductorAcceptanceCohortFocusedAttribution.TrySelect([selector],
            [check with { FailingTestIdentities = [selector] }]);
        return selection is null ? finding : finding with
        {
            Request = selection.Request,
            BaselineCheck = $"reviewer focused evidence: {GoalAcceptanceVerifier.ProjectLabel(finding.Project)} FullyQualifiedName~{selector}"
        };
    }

    internal static AttributedFailure? RestoreIntroduced(IEnumerable<SoloRepairFinding> findings)
    {
        var rows = findings.Where(finding => finding.Feedback is not null)
            .DistinctBy(finding => finding.Identity, StringComparer.Ordinal)
            .OrderBy(finding => finding.Identity, StringComparer.Ordinal).ToArray();
        return rows.Length == 0 ? null : new("solo", rows[0].Attempt, rows[0].Candidate,
            rows.Select(row => row.Identity).ToArray(),
            string.Join(Environment.NewLine, rows.Select(row => $"reproduced {row.Identity}: {row.Feedback}")));
    }

    private static SoloRepairFinding ReadRepairFinding(SoloAcceptanceCheckEvidence item, string identity,
        SoloRepairFinding finding, SoloAcceptanceFailureReceipt receipt, SoloAcceptanceFailureEvidence proof,
        string currency, Lazy<ILookup<string, TrxRow>> candidateRows,
        IReadOnlyDictionary<string, Lazy<IReadOnlyList<TrxRow>>> baselineRows)
    {
        var check = item.Check;
        if (string.IsNullOrWhiteSpace(identity) || identity != identity.Trim() || !Usable(check) || check.Passed ||
            check.Advisory || check.TestResultIsExplicitCrossAttemptReuse || check.TestResultAttemptId != receipt.AttemptId)
            return finding with { Reason = SoloHoldReasons.CandidateNotIntroduced };
        var project = finding.Project;
        var selector = AcceptanceFailureAttributionPlanner.NormalizeIdentity(identity);
        if (finding.Request is null)
            return finding with { Reason = SoloHoldReasons.TestBindingMismatch };
        finding = finding with { Reason = SoloHoldReasons.TestBindingMismatch };
        if (currency.Length != 0) return finding with { Reason = currency };
        TrxRow? candidate;
        try { candidate = ReadExactRow(candidateRows.Value[identity], identity); }
        catch (Exception ex) when (IsRepairEvidenceError(ex)) { return finding with { Reason = ex.Message }; }
        if (candidate?.Value.Outcome != "Failed") return finding with { Reason = SoloHoldReasons.FailingTestsAbsent };
        finding = finding with { CandidateArtifacts = item.Artifacts };
        if (!proof.Baseline.Accepted || proof.Baseline.Disposition == FindingEvidenceArmDisposition.ApparatusFailure)
            return finding with { Reason = SoloHoldReasons.BaselineUnavailable };
        var bindings = proof.Bindings.Where(b => b.CheckName == check.Name && b.TestIdentity == identity).ToArray();
        var attributions = check.FailingTestAttributions?.Where(a => a.TestIdentity == identity).ToArray();
        if (bindings.Length != 1 || attributions is not { Length: 1 }) return finding;
        var baselineName = bindings[0].BaselineCheckName;
        var baselines = proof.BaselineChecks.Where(b => b.Check.Name == baselineName).ToArray();
        if (baselines.Length != 1) return finding;
        var baseline = baselines[0];
        var armCheck = proof.Baseline.Checks.Single(b => b.Name == baselineName);
        if (JsonSerializer.Serialize(armCheck) != JsonSerializer.Serialize(baseline.Check) ||
            baselineName != finding.BaselineCheck ||
            (baseline.Check.TestProjectPath is { } baselineProject && baselineProject != project))
            return finding with { Reason = SoloHoldReasons.BaselineResultMismatch };

        finding = finding with { BaselineCheck = baselineName, BaselineArtifacts = baseline.Artifacts };
        if (baseline.Check.Advisory || baseline.Check.TestResultIsExplicitCrossAttemptReuse)
            return finding with { Reason = SoloHoldReasons.BaselineApparatusOrUnknown };
        var origin = attributions[0].Origin;
        if (attributions[0].CandidateRerun is { } rerun &&
            (rerun.Outcome != "Failed" || rerun.Error is not null ||
             !item.CandidateReruns.TryGetValue(identity, out var artifact) ||
             ReadExactRow(ReadTrx(artifact, receipt.ResultsRoot), identity)?.Value.Outcome != "Failed"))
            return finding with { CandidateArtifacts = [], Reason = SoloHoldReasons.CandidateRerunUnconfirmed };
        if (baseline.Check.BaselineSourceAbsence is { } absence)
        {
            if (origin != AcceptanceTestFailureOrigin.Introduced || absence.BaselineSha != receipt.BaselineSha ||
                absence.Selector != selector || baseline.Check.FailureClassification != AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline ||
                baseline.Check.ExecutedTestCount != 0 || baseline.Check.Passed || baseline.Artifacts.Count != 0)
                return finding with { Reason = SoloHoldReasons.SourceAbsenceInvalid };
        }
        else
        {
            if (!Usable(baseline.Check)) return finding with { Reason = SoloHoldReasons.BaselineApparatusOrUnknown };
            IReadOnlyList<TrxRow> rows;
            try { rows = baselineRows[baselineName].Value; }
            catch (Exception ex) when (IsRepairEvidenceError(ex)) { return finding with { Reason = ex.Message }; }
            var exact = ReadExactRow(rows, identity);
            if (origin == AcceptanceTestFailureOrigin.Inherited && receipt.BaselineSha == receipt.MainSha &&
                proof.Baseline.Disposition == FindingEvidenceArmDisposition.Red && !proof.Baseline.Passed &&
                !baseline.Check.Passed && exact?.Value.Outcome == "Failed")
                return finding with { Inherited = true, Reason = null };
            if (origin != AcceptanceTestFailureOrigin.Introduced) return finding;
            if (rows.Any(row => row.AmbiguousIdentities?.Contains(identity, StringComparer.Ordinal) == true)) return finding;
            var selected = rows.Where(row => row.AmbiguousIdentities is null &&
                AcceptanceFailureAttributionPlanner.NormalizeIdentity(row.Key) == selector).ToArray();
            var introduced = baseline.Check.Passed
                ? exact?.Value.Outcome == "Passed" && rows.All(row => row.Value.Outcome == "Passed")
                : (exact is null || exact.Value.Outcome == "Passed") && selected.Count(row => row.Key == identity) <= 1 &&
                  identity != selector && selected.All(row => row.Key != selector) &&
                  selected.Any(row => row.Value.Outcome == "Failed");
            if (!introduced) return finding with { Reason = SoloHoldReasons.BaselineInheritedOrUncovered };
        }

        return finding with { Reason = null, Feedback = RepairFeedback(candidate) };
    }

    private static string RepairFeedback(TrxRow row)
    {
        var first = row.Value.Message?.Split(['\r', '\n'])[0] ?? "(no message)";
        return (first.Length <= 2000 ? first : first[..1997] + "...") + $" (trx: {row.Path})";
    }

    private static TrxRow? ReadExactRow(IEnumerable<TrxRow> rows, string identity)
    {
        var matches = rows.Where(row => row.Key == identity ||
            row.AmbiguousIdentities?.Contains(identity, StringComparer.Ordinal) == true).ToArray();
        return matches is [{ AmbiguousIdentities: null } exact] ? exact : null;
    }

    internal static SoloRepairCheckReceipt ReadRepairCheck(FocusedEvidenceRunResult result, FindingEvidenceArm arm,
        string revision, SoloRepairFinding finding, string resultsRoot)
    {
        var arms = result.Arms?.Where(item => item.Arm == arm).ToArray();
        if (!result.Accepted || result.Request != finding.Request || arms is not { Length: 1 } || arms[0].Sha != revision ||
            !arms[0].Accepted || arms[0].Disposition != (arms[0].Passed
                ? FindingEvidenceArmDisposition.Green : FindingEvidenceArmDisposition.Red))
            return new("unresolved", "The verifier did not execute a conclusive selection on " + revision, []);
        var check = arms[0].Checks is [var selected] ? selected : null;
        if (check is null || !Usable(check) || check.Advisory || check.TestResultIsExplicitCrossAttemptReuse ||
            (finding.BaselineCheck is not null && finding.BaselineCheck != check.Name) ||
            check.TestResultPaths is not { Count: > 0 } paths)
            return new("unresolved", "The executed selection or its result artifacts are unavailable.", []);
        var artifacts = paths.Select(path =>
        {
            var bytes = File.ReadAllBytes(path);
            var digest = SoloAcceptanceFailureEvidenceWriter.Digest(bytes);
            var retained = Path.Combine(resultsRoot, digest + ".trx");
            File.WriteAllBytes(retained, bytes);
            return new SoloAcceptanceArtifact(retained, digest);
        }).ToArray();
        var rows = ReadRows(new(check, artifacts, new Dictionary<string, SoloAcceptanceArtifact>()), resultsRoot).ValidateRows();
        var exact = ReadExactRow(rows, finding.Identity);
        if (arms[0].Passed && check.Passed && exact?.Value.Outcome == "Passed" &&
            rows.All(row => row.Value.Outcome != "Failed"))
            return new("passed", "The original exact identity passed.", artifacts);
        if (!arms[0].Passed && !check.Passed && exact?.Value.Outcome == "Failed")
        {
            var selector = AcceptanceFailureAttributionPlanner.NormalizeIdentity(finding.Identity);
            var classified = AcceptanceFailureAttributionPlanner.ClassifyBaselineFailures([finding.Identity], [selector],
                new Dictionary<string, string>(StringComparer.Ordinal) { [selector] = check.Name }, arms[0]);
            if (classified.Single().Origin == AcceptanceTestFailureOrigin.Inherited)
                return new("failed", check.Name, artifacts, Feedback: RepairFeedback(exact));
        }
        return new("unresolved", "The verifier did not produce a unique executed row for the original identity.", artifacts);
    }

    internal static SoloRepairCheckReceipt ReadRepairAttribution(FocusedEvidenceRunResult result, string candidateSha,
        string baselineSha, SoloRepairFinding finding, string resultsRoot, bool allowCandidatePass = false)
    {
        var candidate = ReadRepairCheck(result, FindingEvidenceArm.Candidate, candidateSha, finding, resultsRoot);
        var baseline = ReadRepairCheck(result, FindingEvidenceArm.Baseline, baselineSha, finding, resultsRoot);
        if (allowCandidatePass && candidate.Outcome == "passed" && result.Passed && baseline.Outcome is "failed" or "passed")
            return candidate with { Artifacts = baseline.Artifacts, CandidateArtifacts = candidate.Artifacts };
        if (candidate.Outcome != "failed" || result.Passed)
            return new("unresolved", $"The candidate arm at {candidateSha} did not confirm the original exact failure: {candidate.Detail}",
                baseline.Artifacts, candidate.Artifacts);
        return baseline with
        {
            Outcome = baseline.Outcome switch { "failed" => "inherited", "passed" => "introduced", _ => "unresolved" },
            CandidateArtifacts = candidate.Artifacts,
            Feedback = candidate.Feedback
        };
    }

    internal static bool IsRepairEvidenceError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or XmlException or
            ArgumentException or InvalidOperationException or NullReferenceException;
}
