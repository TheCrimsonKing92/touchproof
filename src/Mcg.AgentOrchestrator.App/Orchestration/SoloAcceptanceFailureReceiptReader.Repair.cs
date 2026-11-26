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
    internal const string LaneEvidenceUnavailable = "The verifier did not attest tool execution for this selection.";

    internal static SoloRepairPartition? ReadRepairPartition(Goal goal, SoloAcceptanceConsumptionContext? current,
        AcceptanceVerificationSummary? completion = null)
    {
        var summary = goal.LatestAcceptanceFailure;
        if (summary is null) return null;
        if (summary.SoloFailureReceipt is not { } retained)
            return completion is { RequiresSoloFailureReceipt: true }
                ? UnresolvedChecks(summary, null, completion, SoloHoldReasons.CaptureUnavailable)
                : null;
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
            // A successful strict read accounts for confirmed flakes. A refusal still
            // leaves each unambiguous sibling eligible for its own proof.
            var strict = introducedOnly ? Read(goal, current) : null;
            var checkAttributions = (summary.CheckAttributions ?? []).ToLookup(attr => attr.CheckName, StringComparer.Ordinal);

            var findings = new List<SoloRepairFinding>();
            var baselineRows = proof.BaselineChecks.ToDictionary(item => item.Check.Name,
                item => new Lazy<IReadOnlyList<TrxRow>>(() =>
                    ReadRows(item, receipt.ResultsRoot, partitionAmbiguousRows: true).ValidateRows()),
                StringComparer.Ordinal);
            foreach (var item in proof.Checks)
            {
                var check = item.Check;
                if (SoloCoveredCheckInheritance.CoveredChecks(check, checks) is not null) continue;
                var canPartition = !introducedOnly || checkAttributions[check.Name].ToArray() is
                    [{ Origin: AcceptanceFailureOrigin.Introduced, Cause: AcceptanceFailureCause.NotClassified }];
                var candidateRows = new Lazy<ILookup<string, TrxRow>>(() =>
                    ReadRows(item, receipt.ResultsRoot, partitionAmbiguousRows: true)
                    .ValidateRows().SelectMany(row => (row.AmbiguousIdentities ?? [row.Key])
                        .Select(key => (Key: key, Row: row))).ToLookup(item => item.Key, item => item.Row, StringComparer.Ordinal));
                var identities = (check.FailingTestIdentities ?? []).DefaultIfEmpty("").Distinct(StringComparer.Ordinal);
                foreach (var identity in identities)
                {
                    var finding = BindRepairSelection(NewUnresolvedFinding(receipt.CandidateSha, receipt.MainSha,
                        receipt.BaselineSha, receipt.AttemptId, receipt.Evidence, check.Name, identity, check.TestProjectPath,
                        SoloHoldReasons.BaselineInheritedOrUncovered), check) with { CandidateArtifacts = item.Artifacts };
                    try
                    {
                        if (!finding.LaneEnvironmentVerified)
                            findings.Add(finding with { Reason = LaneEvidenceUnavailable });
                        else if (strict?.Failure is not null)
                        {
                            if (strict.IntroducedRowFeedback?.TryGetValue((check.Name, identity), out var feedback) != true) continue;
                            var binding = proof.Bindings.Single(b => b.CheckName == check.Name && b.TestIdentity == identity);
                            var baseline = proof.BaselineChecks.Single(b => b.Check.Name == binding.BaselineCheckName);
                            var baselineAttested = baseline.Check.LaneToolResolution is { MissingTool: null } ||
                                baseline.Check is { LaneToolResolution: null, BaselineSourceAbsence: not null };
                            findings.Add(finding with { Reason = baselineAttested ? null : LaneEvidenceUnavailable,
                                Feedback = baselineAttested ? feedback : null,
                                ProofBaselineCheck = binding.BaselineCheckName == finding.BaselineCheck ? null : binding.BaselineCheckName,
                                CandidateArtifacts = item.Artifacts, BaselineArtifacts = baseline.Artifacts });
                        }
                        else if (!canPartition)
                            findings.Add(finding with { Reason = SoloHoldReasons.AuthoritativeAttributionMismatch });
                        else
                            findings.Add(ReadRepairFinding(item, identity, finding, receipt, proof, reason, candidateRows, baselineRows));
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

    internal static IReadOnlyList<SoloRepairFinding> ReadUnresolvedCompletion(AcceptanceVerificationSummary completion)
    {
        var observed = completion.RequiredUnmetCriteria.ToLookup(check => check.Name, StringComparer.Ordinal);
        var failedChecks = completion.FailedChecks ?? observed.Select(check => check.Key).ToArray();
        return UnresolvedFindings(failedChecks, completion.BranchHeadSha, completion.MainHeadSha, completion.AttemptId,
            observed, completion.SoloFailureReceipt?.Evidence, SoloHoldReasons.CaptureUnavailable)
            .Where(finding => finding.Identity.Length > 0).ToArray();
    }

    private static SoloRepairPartition UnresolvedChecks(AcceptanceFailureSummary summary, SoloAcceptanceFailureReceipt? receipt,
        AcceptanceVerificationSummary? completion, string reason)
    {
        var observed = completion is not null && completion.BranchHeadSha == summary.BranchHeadSha &&
            completion.MainHeadSha == summary.MainHeadSha && completion.AttemptId == summary.AttemptId
            ? completion.RequiredUnmetCriteria.ToLookup(check => check.Name, StringComparer.Ordinal) : null;
        return new(UnresolvedFindings(summary.FailedChecks, summary.BranchHeadSha, summary.MainHeadSha,
            summary.AttemptId, observed, receipt?.Evidence, reason).ToArray(), null);
    }

    private static IEnumerable<SoloRepairFinding> UnresolvedFindings(IReadOnlyList<string> failedChecks,
        string? candidate, string? main, string? attempt, ILookup<string, AcceptanceCheckResult>? observed,
        SoloAcceptanceArtifact? proof, string reason)
    {
        foreach (var name in failedChecks)
        {
            var checks = observed?[name].ToArray();
            var check = checks is [{ Passed: false, Advisory: false } only] ? only : null;
            foreach (var identity in (check?.FailingTestIdentities ?? []).DefaultIfEmpty("").Distinct(StringComparer.Ordinal))
            {
                var finding = NewUnresolvedFinding(candidate ?? "", main ?? "", "",
                    attempt ?? "", proof, name, identity, check?.TestProjectPath, reason);
                yield return check is null ? finding : BindRepairSelection(finding, check) with
                {
                    Reason = check.FailureClassification == AcceptanceFailureClassifications.MissingTool
                        ? LaneEvidenceUnavailable : reason
                };
            }
        }
    }

    private static SoloRepairFinding NewUnresolvedFinding(string candidate, string main, string baseline, string attempt,
        SoloAcceptanceArtifact? proof, string check, string identity, string? project, string reason) =>
        new(candidate, main, baseline, check, identity, project, null, null, false, attempt, proof, [], [], reason);

    private static SoloRepairFinding BindRepairSelection(SoloRepairFinding finding, AcceptanceCheckResult check)
    {
        finding = finding with { LaneEnvironmentVerified = check.LaneToolResolution is { MissingTool: null } };
        if (string.IsNullOrWhiteSpace(finding.Identity) || finding.Identity != finding.Identity.Trim() ||
            string.IsNullOrWhiteSpace(finding.Project)) return finding;
        var selector = AcceptanceFailureAttributionPlanner.NormalizeIdentity(finding.Identity);
        var selection = ConductorAcceptanceCohortFocusedAttribution.TrySelect([selector],
            [check with { FailingTestIdentities = [selector] }]);
        var request = $"{finding.Project}: FullyQualifiedName~{selector}";
        return selection is null ? finding : finding with
        {
            Request = request,
            LegacyRequest = request == selection.Request ? null : selection.Request,
            BaselineCheck = FocusedEvidenceManifestChecks.FocusedTestCheckName(GoalAcceptanceVerifier.ProjectLabel(finding.Project),
                $"FullyQualifiedName~{selector}")
        };
    }

    internal static SoloRepairFinding BindRetainedRepairRequest(SoloRepairFinding finding)
    {
        if (string.IsNullOrWhiteSpace(finding.Project)) return finding;
        var selector = AcceptanceFailureAttributionPlanner.NormalizeIdentity(finding.Identity);
        var filter = $"FullyQualifiedName~{selector}";
        return finding.Request == $"{GoalAcceptanceVerifier.ProjectLabel(finding.Project)}: {filter}"
            ? finding with { Request = $"{finding.Project}: {filter}" }
            : finding;
    }

    private static bool RepairCheckNameMatches(SoloRepairFinding finding, string actual)
    {
        if (finding.BaselineCheck == actual) return true;
        if (finding.Project is not { } project) return false;
        var filter = $"FullyQualifiedName~{AcceptanceFailureAttributionPlanner.NormalizeIdentity(finding.Identity)}";
        var shortName = FocusedEvidenceManifestChecks.FocusedTestCheckName(GoalAcceptanceVerifier.ProjectLabel(project), filter);
        var pathName = FocusedEvidenceManifestChecks.FocusedTestCheckName(project.Replace('\\', '/'), filter);
        return (finding.BaselineCheck == shortName && actual == pathName) ||
            (finding.BaselineCheck == pathName && actual == shortName);
    }

    private static bool MatchesRepairProject(string? expected, string? executed) =>
        !string.IsNullOrWhiteSpace(expected) && executed?.Replace('\\', '/') == expected.Replace('\\', '/');

    internal static AttributedFailure? RestoreIntroduced(IEnumerable<SoloRepairFinding> findings)
    {
        var rows = findings.Where(finding => finding.Feedback is not null)
            .DistinctBy(finding => finding.Identity, StringComparer.Ordinal)
            .OrderBy(finding => finding.Identity, StringComparer.Ordinal).ToArray();
        return rows.Length == 0 ? null : new("solo", rows[0].Attempt, rows[0].Candidate,
            rows.Select(row => row.Identity).ToArray(),
            string.Join(Environment.NewLine, rows.Select(row => $"reproduced {row.Identity}: {row.Feedback}")));
    }

    internal static void VerifyRetainedRepairProof(string goal, SoloRepairFinding finding)
    {
        var envelope = finding.Proof ?? throw new InvalidDataException(SoloHoldReasons.ProofIdentityMismatch);
        var proofDirectory = Path.GetDirectoryName(Path.GetFullPath(envelope.Path))!;
        var proof = JsonSerializer.Deserialize<SoloAcceptanceFailureEvidence>(ReadArtifact(envelope, proofDirectory));
        var receipt = proof?.Identity;
        if (receipt is not { ContractVersion: 1, CaptureError: null } || receipt.GoalId != goal ||
            receipt.AttemptId != finding.Attempt || receipt.CandidateSha != finding.Candidate ||
            receipt.MainSha != finding.Main || receipt.BaselineSha != finding.Baseline ||
            receipt.Evidence != (envelope with { Sha256 = "" }) ||
            Path.GetFullPath(receipt.ResultsRoot) != proofDirectory)
            throw new InvalidDataException(SoloHoldReasons.ProofIdentityMismatch);

        var check = proof!.Checks.Single(item => item.Check.Name == finding.Check);
        var baseline = proof.BaselineChecks.Single(item => item.Check.Name == (finding.ProofBaselineCheck ?? finding.BaselineCheck));
        if (check.Check.LaneToolResolution is not { MissingTool: null } ||
            (baseline.Check.LaneToolResolution is not { MissingTool: null } &&
             (finding.Inherited || baseline.Check is not { LaneToolResolution: null, BaselineSourceAbsence: not null })))
            throw new InvalidDataException(LaneEvidenceUnavailable);
        if ((finding.Inherited || baseline.Check.BaselineSourceAbsence is null) &&
            !MatchesRepairProject(finding.Project, baseline.Check.TestProjectPath))
            throw new InvalidDataException(SoloHoldReasons.BaselineResultMismatch);
        if (!check.Artifacts.SequenceEqual(finding.CandidateArtifacts) ||
            !baseline.Artifacts.SequenceEqual(finding.BaselineArtifacts))
            throw new InvalidDataException(SoloHoldReasons.ProofIdentityMismatch);
        foreach (var artifact in finding.CandidateArtifacts.Concat(finding.BaselineArtifacts))
            ReadArtifact(artifact, receipt.ResultsRoot);
        if (check.CandidateReruns.TryGetValue(finding.Identity, out var rerun))
            ReadArtifact(rerun, receipt.ResultsRoot);
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
        // A tool launch fault or build timeout can explain the arm disposition without invalidating siblings.
        if (!proof.Baseline.Accepted ||
            (proof.Baseline.Disposition == FindingEvidenceArmDisposition.ApparatusFailure &&
             !CanPartitionBaselineApparatusFailure(proof.Baseline.Checks)))
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
            !RepairCheckNameMatches(finding, baselineName))
            return finding with { Reason = SoloHoldReasons.BaselineResultMismatch };

        finding = finding with { ProofBaselineCheck = baselineName == finding.BaselineCheck ? null : baselineName,
            BaselineArtifacts = baseline.Artifacts };
        if (baseline.Check.Advisory || baseline.Check.TestResultIsExplicitCrossAttemptReuse ||
            baseline.Check.LaneToolResolution?.MissingTool is not null)
            return finding with { Reason = SoloHoldReasons.BaselineApparatusOrUnknown };
        var origin = attributions[0].Origin;
        if (attributions[0].CandidateRerun is { } rerun &&
            (rerun.Outcome != "Failed" || rerun.Error is not null ||
             !item.CandidateReruns.TryGetValue(identity, out var artifact) ||
             ReadExactRow(ReadTrx(artifact, receipt.ResultsRoot, partitionAmbiguousRows: true), identity)?.Value.Outcome != "Failed"))
            return finding with { CandidateArtifacts = [], Reason = SoloHoldReasons.CandidateRerunUnconfirmed };
        if (baseline.Check.BaselineSourceAbsence is { } absence)
        {
            if (baseline.Check.TestProjectPath is { } baselineProject && !MatchesRepairProject(project, baselineProject))
                return finding with { Reason = SoloHoldReasons.BaselineResultMismatch };
            if (origin != AcceptanceTestFailureOrigin.Introduced || absence.BaselineSha != receipt.BaselineSha ||
                absence.Selector != selector || baseline.Check.FailureClassification != AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline ||
                baseline.Check.ExecutedTestCount != 0 || baseline.Check.Passed || baseline.Artifacts.Count != 0)
                return finding with { Reason = SoloHoldReasons.SourceAbsenceInvalid };
        }
        else
        {
            if (!Usable(baseline.Check)) return finding with { Reason = SoloHoldReasons.BaselineApparatusOrUnknown };
            if (!MatchesRepairProject(project, baseline.Check.TestProjectPath))
                return finding with { Reason = SoloHoldReasons.BaselineResultMismatch };
            if (baseline.Check.LaneToolResolution is not { MissingTool: null })
                return finding with { Reason = LaneEvidenceUnavailable };
            IReadOnlyList<TrxRow> rows;
            try { rows = baselineRows[baselineName].Value; }
            catch (Exception ex) when (IsRepairEvidenceError(ex)) { return finding with { Reason = ex.Message }; }
            var exact = ReadExactRow(rows, identity);
            if (origin == AcceptanceTestFailureOrigin.Inherited && receipt.BaselineSha == receipt.MainSha &&
                proof.Baseline.Disposition is FindingEvidenceArmDisposition.Red or FindingEvidenceArmDisposition.ApparatusFailure &&
                !proof.Baseline.Passed &&
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
                  !rows.Any(row => row.AmbiguousIdentities?.Contains(selector, StringComparer.Ordinal) == true) &&
                  selected.Any(row => row.Value.Outcome == "Failed");
            if (!introduced) return finding with { Reason = SoloHoldReasons.BaselineInheritedOrUncovered };
        }

        return finding with { Reason = null, Feedback = RepairFeedback(candidate) };
    }

    private static bool CanPartitionBaselineApparatusFailure(IReadOnlyList<AcceptanceCheckResult> checks)
    {
        var faults = checks.Where(FocusedEvidenceExecution.IsFocusedEvidenceApparatusFailure).ToArray();
        return faults.Length > 0 && faults.All(check => check.FailureClassification is
            AcceptanceFailureClassifications.MissingTool or AcceptanceFailureClassifications.BuildPhaseTimeout);
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
        if (arms is [{ Checks: [var executed] }] && executed.LaneToolResolution?.MissingTool is { } missingTool)
            return new("unresolved", $"Acceptance lane environment cannot resolve {missingTool}.", []);
        if (!result.Accepted || result.Request != finding.Request || arms is not { Length: 1 } || arms[0].Sha != revision ||
            !arms[0].Accepted || arms[0].Disposition != (arms[0].Passed
                ? FindingEvidenceArmDisposition.Green : FindingEvidenceArmDisposition.Red))
            return new("unresolved", "The verifier did not execute a conclusive selection on " + revision, []);
        var check = arms[0].Checks is [var selected] ? selected : null;
        if (check is null || check.LaneToolResolution is not { MissingTool: null } ||
            !Usable(check) || check.Advisory || check.TestResultIsExplicitCrossAttemptReuse ||
            (finding.BaselineCheck is not null && !RepairCheckNameMatches(finding, check.Name)) ||
            check.TestResultPaths is not { Count: > 0 } paths)
            return new("unresolved", "The executed selection or its result artifacts are unavailable.", []);
        if (!MatchesRepairProject(finding.Project, check.TestProjectPath))
            return new("unresolved", "The focused execution does not identify the original test project.", []);

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
            return new("passed", "The original exact identity passed.", artifacts) { LaneEnvironmentVerified = true };
        if (!arms[0].Passed && !check.Passed && exact?.Value.Outcome == "Failed")
        {
            var selector = AcceptanceFailureAttributionPlanner.NormalizeIdentity(finding.Identity);
            var classified = AcceptanceFailureAttributionPlanner.ClassifyBaselineFailures([finding.Identity], [selector],
                new Dictionary<string, string>(StringComparer.Ordinal) { [selector] = check.Name }, arms[0]);
            if (classified.Single().Origin == AcceptanceTestFailureOrigin.Inherited)
                return new("failed", check.Name, artifacts, Feedback: RepairFeedback(exact)) { LaneEnvironmentVerified = true };
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
