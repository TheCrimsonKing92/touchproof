using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SoloAcceptanceConsumptionContext(string CandidateSha, string MainSha,
    string CandidateTreeSha, string BaselineSha);
internal sealed record SoloAcceptanceFailureRead(AttributedFailure? Failure, string Reason);

// This reads retained evidence only. Attribution execution remains in the verifier.
internal static class SoloAcceptanceFailureReceiptReader
{
    internal static SoloAcceptanceFailureRead Read(Goal goal, SoloAcceptanceConsumptionContext? current)
    {
        var summary = goal.LatestAcceptanceFailure;
        if (summary?.SoloFailureReceipt is not { } receipt) return Refuse("solo-receipt-absent");
        if (current is null) return Refuse("solo-current-identity-unavailable");
        if (receipt.ContractVersion != 1 || receipt.CaptureError is not null)
            return Refuse("solo-evidence-capture-unavailable: " + receipt.CaptureError);
        if (receipt.GoalId != goal.Id.Value || string.IsNullOrWhiteSpace(receipt.AttemptId) ||
            receipt.AttemptId != summary.AttemptId) return Refuse("solo-goal-or-attempt-mismatch");
        if (receipt.CandidateSha != summary.BranchHeadSha || receipt.MainSha != summary.MainHeadSha ||
            receipt.CandidateSha != current.CandidateSha || receipt.MainSha != current.MainSha ||
            receipt.CandidateTreeSha != current.CandidateTreeSha || receipt.BaselineSha != current.BaselineSha ||
            new[] { receipt.CandidateSha, receipt.CandidateTreeSha, receipt.MainSha, receipt.BaselineSha }
                .Any(string.IsNullOrWhiteSpace)) return Refuse("solo-revision-mismatch");
        try
        {
            var proof = JsonSerializer.Deserialize<SoloAcceptanceFailureEvidence>(ReadArtifact(receipt.Evidence, receipt.ResultsRoot))
                ?? throw new InvalidDataException("solo-proof-null");
            // The inner identity was written before the envelope digest existed.
            if (proof.Identity != receipt with { Evidence = receipt.Evidence with { Sha256 = "" } })
                return Refuse("solo-proof-identity-mismatch");
            if (proof.Checks is null || proof.BaselineChecks is null || proof.Bindings is null || proof.Baseline is null ||
                proof.Baseline.Sha != receipt.BaselineSha || proof.Baseline.Arm != FindingEvidenceArm.Baseline ||
                !proof.Baseline.Accepted || proof.Baseline.Disposition == FindingEvidenceArmDisposition.ApparatusFailure)
                return Refuse("solo-baseline-unavailable");
            if (!SameNames(proof.Checks.Select(item => item.Check.Name), summary.FailedChecks) ||
                !SameNames(proof.BaselineChecks.Select(item => item.Check.Name), proof.Baseline.Checks.Select(check => check.Name)))
                return Refuse("solo-check-mismatch");
            var feedback = new SortedDictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in proof.Checks)
            {
                var check = item.Check;
                if (!Usable(check) || check.Passed || check.Advisory || check.TestResultIsExplicitCrossAttemptReuse ||
                    check.TestResultAttemptId != receipt.AttemptId || check.FailingTestIdentities is not { Count: > 0 } identities ||
                    check.FailingTestAttributions is null ||
                    !SameNames(check.FailingTestAttributions.Select(attr => attr.TestIdentity), identities) ||
                    check.FailingTestAttributions.Any(attr => attr.Origin != AcceptanceTestFailureOrigin.Introduced))
                    return Refuse("solo-candidate-not-introduced");
                var attribution = summary.CheckAttributions?.Where(attr => attr.CheckName == check.Name).ToArray();
                if (attribution is not { Length: 1 } || attribution[0].Origin != AcceptanceFailureOrigin.Introduced ||
                    attribution[0].Cause != AcceptanceFailureCause.NotClassified)
                    return Refuse("solo-authoritative-attribution-mismatch");
                var rows = ReadRows(item, receipt.ResultsRoot);
                if (!SameNames(rows.Where(row => row.Value.Outcome == "Failed").Select(row => row.Key), identities))
                    return Refuse("solo-failing-tests-mismatch");
                foreach (var identity in identities)
                {
                    var bindings = proof.Bindings.Where(binding => binding.CheckName == check.Name && binding.TestIdentity == identity).ToArray();
                    if (bindings.Length != 1) return Refuse("solo-test-binding-mismatch");
                    var baselineChecks = proof.BaselineChecks.Where(b => b.Check.Name == bindings[0].BaselineCheckName).ToArray();
                    if (baselineChecks.Length != 1) return Refuse("solo-baseline-check-mismatch");
                    var baseline = baselineChecks[0];
                    // Recheck the persisted authoritative arm and the retained check as well.
                    var armChecks = proof.Baseline.Checks.Where(b => b.Name == baseline.Check.Name).ToArray();
                    if (armChecks.Length != 1 || JsonSerializer.Serialize(armChecks[0]) != JsonSerializer.Serialize(baseline.Check))
                        return Refuse("solo-baseline-result-mismatch");
                    var selector = AcceptanceFailureAttributionPlanner.NormalizeIdentity(identity);
                    if (baseline.Check.BaselineSourceAbsence is { } absent)
                    {
                        if (absent.BaselineSha != receipt.BaselineSha || absent.Selector != selector ||
                            baseline.Check.FailureClassification != AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline ||
                            baseline.Check.ExecutedTestCount != 0 || baseline.Check.Passed || baseline.Artifacts.Count != 0)
                            return Refuse("solo-source-absence-invalid");
                    }
                    else
                    {
                        if (!Usable(baseline.Check)) return Refuse("solo-baseline-apparatus-or-unknown");
                        var baselineRows = ReadRows(baseline, receipt.ResultsRoot);
                        var selected = baselineRows.Where(row => AcceptanceFailureAttributionPlanner.NormalizeIdentity(row.Key) == selector).ToArray();
                        if (selected.Length == 0 || selected.Any(row => row.Key == identity && row.Value.Outcome == "Failed"))
                            return Refuse("solo-baseline-inherited-or-uncovered");
                        if (baseline.Check.Passed)
                        {
                            if (!baselineRows.TryGetValue(identity, out var exact) || exact.Outcome != "Passed" ||
                                baselineRows.Any(row => row.Value.Outcome != "Passed")) return Refuse("solo-baseline-green-uncovered");
                        }
                        else if (identity == selector || selected.Any(row => row.Key == AcceptanceFailureAttributionPlanner.NormalizeIdentity(row.Key)) ||
                            !selected.Any(row => row.Value.Outcome == "Failed")) return Refuse("solo-data-case-ambiguous");
                    }
                    var rerun = check.FailingTestAttributions.Single(attr => attr.TestIdentity == identity).CandidateRerun;
                    if (rerun is not null)
                    {
                        if (rerun.Outcome != "Failed" || rerun.Error is not null ||
                            !item.CandidateReruns.TryGetValue(identity, out var artifact) ||
                            !ReadTrx(artifact, receipt.ResultsRoot).TryGetValue(identity, out var repeated) || repeated.Outcome != "Failed")
                            return Refuse("solo-candidate-rerun-unconfirmed");
                    }
                    var message = rows[identity].Message;
                    var first = message?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')[0]
                        ?? "(no message)";
                    feedback[identity] = first.Length <= 2000 ? first : first[..1997] + "...";
                }
            }
            return feedback.Count == 0 ? Refuse("solo-failing-tests-absent") : new(
                new("solo", receipt.AttemptId, receipt.CandidateSha, feedback.Keys.ToArray(),
                    string.Join(Environment.NewLine, feedback.Select(row => $"reproduced {row.Key}: {row.Value}"))), "solo-evidence-validated");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or XmlException or
            ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return Refuse("solo-evidence-invalid: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static bool Usable(AcceptanceCheckResult check) =>
        check.FailureCauseEvidence?.Cause is null or AcceptanceFailureCause.NotClassified &&
        check.FailureClassification is null or AcceptanceShardCompletionPredicates.FailingTrx &&
        check.ExecutedTestCount is > 0 && check.CompletionDecision?.TimedOut != true &&
        (check.Passed ? check.ExitCode == 0 : check.ExitCode is > 0);

    private static Dictionary<string, AcceptanceTrxFailure> ReadRows(SoloAcceptanceCheckEvidence check, string root)
    {
        if (check.Artifacts is not { Count: > 0 }) throw new InvalidDataException("solo-trx-absent");
        var rows = new Dictionary<string, AcceptanceTrxFailure>(StringComparer.Ordinal);
        foreach (var artifact in check.Artifacts)
            foreach (var row in ReadTrx(artifact, root))
                if (!rows.TryAdd(row.Key, row.Value)) throw new InvalidDataException("solo-duplicate-test-row");
        if (rows.Count != check.Check.ExecutedTestCount ||
            check.Check.DiscoveredTestCount is { } discovered && discovered != rows.Count ||
            !SameNames(rows.Where(row => row.Value.Outcome == "Failed").Select(row => row.Key), check.Check.FailingTestIdentities ?? []))
            throw new InvalidDataException("solo-incomplete-check-rows");
        return rows;
    }

    private static Dictionary<string, AcceptanceTrxFailure> ReadTrx(SoloAcceptanceArtifact artifact, string root)
    {
        using var stream = new MemoryStream(ReadArtifact(artifact, root), writable: false);
        var doc = XDocument.Load(stream);
        if (doc.Root?.Name.LocalName != "TestRun" || string.IsNullOrWhiteSpace(doc.Root.Attribute("id")?.Value))
            throw new InvalidDataException("solo-trx-run-identity-missing");
        var definitions = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in doc.Descendants().Where(e => e.Name.LocalName == "UnitTest"))
            if (definition.Attribute("id")?.Value is not { Length: > 0 } id || !definitions.TryAdd(id, definition))
                throw new InvalidDataException("solo-trx-ambiguous-definition");
        var rows = new Dictionary<string, AcceptanceTrxFailure>(StringComparer.Ordinal);
        // Reuse the retained reader's exact test-name and message interpretation.
        var failures = AcceptanceTrxFailureReader.Read(doc, artifact.Path).Failures;
        foreach (var row in doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            if (!definitions.TryGetValue(row.Attribute("testId")?.Value ?? "", out var definition))
                throw new InvalidDataException("solo-trx-definition-missing");
            var identity = AcceptanceTrxTestIdentityResolver.Resolve(row, definition);
            var outcome = row.Attribute("outcome")?.Value;
            if (string.IsNullOrWhiteSpace(identity) || outcome is not ("Passed" or "Failed"))
                throw new InvalidDataException("solo-trx-nonexecuted-row");
            var value = outcome == "Passed" ? new AcceptanceTrxFailure(identity, outcome, null, null)
                : failures.Single(failure => failure.TestName == identity);
            if (!rows.TryAdd(identity, value)) throw new InvalidDataException("solo-trx-ambiguous-row");
        }
        if (rows.Count == 0) throw new InvalidDataException("solo-trx-empty");
        return rows;
    }

    private static byte[] ReadArtifact(SoloAcceptanceArtifact artifact, string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(artifact.Path).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("solo-foreign-artifact");
        var bytes = File.ReadAllBytes(artifact.Path);
        if (SoloAcceptanceFailureEvidenceWriter.Digest(bytes) != artifact.Sha256) throw new InvalidDataException("solo-artifact-digest-mismatch");
        return bytes;
    }

    private static bool SameNames(IEnumerable<string> left, IEnumerable<string> right)
    {
        var a = left.Order(StringComparer.Ordinal).ToArray();
        var b = right.Order(StringComparer.Ordinal).ToArray();
        return a.Distinct(StringComparer.Ordinal).Count() == a.Length && a.SequenceEqual(b, StringComparer.Ordinal);
    }
    private static SoloAcceptanceFailureRead Refuse(string reason) => new(null, reason);
}
