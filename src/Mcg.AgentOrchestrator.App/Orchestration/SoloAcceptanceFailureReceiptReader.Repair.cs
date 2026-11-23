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
    internal static SoloRepairPartition? ReadRepairPartition(Goal goal, SoloAcceptanceConsumptionContext? current)
    {
        var summary = goal.LatestAcceptanceFailure;
        if (summary?.SoloFailureReceipt is not { } retained) return null;
        try
        {
            var (receipt, proof, reason) = ReadProof(goal, current, retainUnresolvedIdentities: true);
            if (receipt is null || proof is null)
                return UnresolvedChecks(summary.FailedChecks, retained, reason);

            if (proof.Checks.All(item => item.Check.FailingTestAttributions is { Count: > 0 } origins &&
                    origins.All(origin => origin.Origin == AcceptanceTestFailureOrigin.Introduced) ||
                    SoloCoveredCheckInheritance.CoveredChecks(item.Check, proof.Checks.Select(c => c.Check).ToArray()) is not null))
                return null; // The established introduced-only path also handles confirmed flakes.

            var findings = new List<SoloRepairFinding>();
            var checks = proof.Checks.Select(c => c.Check).ToArray();
            var baselineRows = proof.BaselineChecks.ToDictionary(item => item.Check.Name,
                item => new Lazy<IReadOnlyList<TrxRow>>(() => ReadRows(item, receipt.ResultsRoot).ValidateRows()),
                StringComparer.Ordinal);
            foreach (var item in proof.Checks)
            {
                var check = item.Check;
                if (SoloCoveredCheckInheritance.CoveredChecks(check, checks) is not null) continue;
                var candidateRows = new Lazy<ILookup<string, TrxRow>>(() => ReadRows(item, receipt.ResultsRoot)
                    .ValidateRows().ToLookup(row => row.Key, StringComparer.Ordinal));
                var identities = (check.FailingTestIdentities ?? []).DefaultIfEmpty("").Distinct(StringComparer.Ordinal);
                foreach (var identity in identities)
                {
                    var finding = new SoloRepairFinding(receipt.CandidateSha, receipt.MainSha, receipt.BaselineSha,
                        check.Name, identity, check.TestProjectPath, null, null, false, receipt.AttemptId, receipt.Evidence, [], [],
                        SoloHoldReasons.BaselineInheritedOrUncovered);
                    try
                    {
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
            return UnresolvedChecks(summary.FailedChecks, retained, ex.Message);
        }
    }

    private static SoloRepairPartition UnresolvedChecks(IReadOnlyList<string> checks, SoloAcceptanceFailureReceipt receipt,
        string reason) => new(checks.Select(check => new SoloRepairFinding(receipt.CandidateSha, receipt.MainSha,
            receipt.BaselineSha, check, "", null, null, null, false, receipt.AttemptId, receipt.Evidence, [], [], reason)).ToArray(), null);

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
        var candidate = ReadExactRow(candidateRows.Value[identity], identity);
        if (candidate?.Value.Outcome != "Failed") return finding with { Reason = SoloHoldReasons.FailingTestsAbsent };
        var project = check.TestProjectPath;
        var selector = AcceptanceFailureAttributionPlanner.NormalizeIdentity(identity);
        var selection = ConductorAcceptanceCohortFocusedAttribution.TrySelect([selector],
            [check with { FailingTestIdentities = [selector] }]);
        if (string.IsNullOrWhiteSpace(project) || selection is null)
            return finding with { Reason = SoloHoldReasons.TestBindingMismatch };
        finding = finding with
        {
            Request = selection.Request, CandidateArtifacts = item.Artifacts,
            Reason = SoloHoldReasons.TestBindingMismatch
        };
        if (currency.Length != 0) return finding with { CandidateArtifacts = [], Reason = currency };
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
            baselineName != $"reviewer focused evidence: {GoalAcceptanceVerifier.ProjectLabel(project)} FullyQualifiedName~{selector}" ||
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
            var rows = baselineRows[baselineName].Value;
            var exact = ReadExactRow(rows, identity);
            if (origin == AcceptanceTestFailureOrigin.Inherited && receipt.BaselineSha == receipt.MainSha &&
                proof.Baseline.Disposition == FindingEvidenceArmDisposition.Red && !proof.Baseline.Passed &&
                !baseline.Check.Passed && exact?.Value.Outcome == "Failed")
                return finding with { Inherited = true, Reason = null };
            if (origin != AcceptanceTestFailureOrigin.Introduced) return finding;
            var selected = rows.Where(row => AcceptanceFailureAttributionPlanner.NormalizeIdentity(row.Key) == selector).ToArray();
            var introduced = baseline.Check.Passed
                ? exact?.Value.Outcome == "Passed" && rows.All(row => row.Value.Outcome == "Passed")
                : (exact is null || exact.Value.Outcome == "Passed") && selected.Count(row => row.Key == identity) <= 1 &&
                  identity != selector && selected.All(row => row.Key != selector) &&
                  selected.Any(row => row.Value.Outcome == "Failed");
            if (!introduced) return finding with { Reason = SoloHoldReasons.BaselineInheritedOrUncovered };
        }

        var first = candidate.Value.Message?.Split(['\r', '\n'])[0] ?? "(no message)";
        var feedback = (first.Length <= 2000 ? first : first[..1997] + "...") + $" (trx: {candidate.Path})";
        return finding with { Reason = null, Feedback = feedback };
    }

    private static TrxRow? ReadExactRow(IEnumerable<TrxRow> rows, string identity)
    {
        var matches = rows.Where(row => row.Key == identity).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal static SoloRepairCheckReceipt ReadRepairCheck(FocusedEvidenceRunResult result, FindingEvidenceArm arm,
        string revision, SoloRepairFinding finding, string resultsRoot)
    {
        var arms = result.Arms?.Where(item => item.Arm == arm).ToArray();
        if (result.Request != finding.Request || arms is not { Length: 1 } || arms[0].Sha != revision ||
            !arms[0].Accepted || arms[0].Disposition is not (FindingEvidenceArmDisposition.Green or FindingEvidenceArmDisposition.Red))
            return new("unresolved", "The verifier did not execute a conclusive selection on " + revision, []);
        var check = arms[0].Checks.SingleOrDefault();
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
                return new("failed", check.Name, artifacts);
        }
        return new("unresolved", "The verifier did not produce a unique executed row for the original identity.", artifacts);
    }

    internal static bool IsRepairEvidenceError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or XmlException or
            ArgumentException or InvalidOperationException or NullReferenceException;
}
