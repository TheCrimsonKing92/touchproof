using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record SoloAcceptanceConsumptionContext(string CandidateSha, string MainSha,
    string CandidateTreeSha, string BaselineSha);
internal sealed record SoloAcceptanceFailureRead(AttributedFailure? Failure, string Reason)
{
    internal IReadOnlyList<string> DroppedFlakeIdentities { get; init; } = [];
    internal IReadOnlyDictionary<(string Check, string Identity), string>? IntroducedRowFeedback { get; init; }
}

// This reads retained evidence only. Attribution execution remains in the verifier.
internal static partial class SoloAcceptanceFailureReceiptReader
{
    internal static SoloAcceptanceFailureRead Read(Goal goal, SoloAcceptanceConsumptionContext? current)
    {
        try
        {
            var (receipt, proof, reason) = ReadProof(goal, current);
            if (receipt is null || proof is null) return Refuse(reason);
            var summary = goal.LatestAcceptanceFailure!;
            var feedback = new SortedDictionary<string, string>(StringComparer.Ordinal);
            var rowFeedback = new Dictionary<(string Check, string Identity), string>();
            var checks = proof.Checks.Select(item => item.Check).ToArray();
            var candidateChecks = new List<(SoloAcceptanceCheckEvidence Evidence, SoloAcceptanceFlakePartition Partition)>();
            foreach (var item in proof.Checks)
            {
                var check = item.Check;
                // Direct covered checks have identities and undergo this loop's full validation; their refusals return unchanged.
                if (SoloCoveredCheckInheritance.CoveredChecks(check, checks) is not null) continue;
                if (!Usable(check))
                    return NotIntroduced("unusable-check", check,
                        $"passed={check.Passed}; exitCode={check.ExitCode}; executed={check.ExecutedTestCount}; " +
                        $"classification={DiagnosticValue(check.FailureClassification)}; cause={check.FailureCauseEvidence?.Cause}; " +
                        $"timedOut={check.CompletionDecision?.TimedOut}");
                if (check.Passed) return NotIntroduced("passed-check", check);
                if (check.Advisory) return NotIntroduced("advisory-check", check);
                if (check.TestResultIsExplicitCrossAttemptReuse) return NotIntroduced("cross-attempt-reuse", check);
                if (check.TestResultAttemptId != receipt.AttemptId)
                    return NotIntroduced("foreign-test-attempt", check,
                        $"testAttempt={DiagnosticValue(check.TestResultAttemptId)}; receiptAttempt={receipt.AttemptId}");
                if (check.FailingTestIdentities is not { Count: > 0 } identities)
                    return NotIntroduced("failing-identities-absent", check,
                        $"identities={(check.FailingTestIdentities is null ? "null" : "empty")}");
                if (check.FailingTestAttributions is null) return NotIntroduced("attributions-missing", check);
                if (!SameNames(check.FailingTestAttributions.Select(attr => attr.TestIdentity), identities))
                    return NotIntroduced("attributions-mismatch", check, AttributionMismatch(check, identities));
                if (check.FailingTestAttributions.Any(attr => attr.Origin != AcceptanceTestFailureOrigin.Introduced))
                    return NotIntroduced("non-introduced-origin", check, string.Join("; ",
                        check.FailingTestAttributions.Where(attr => attr.Origin != AcceptanceTestFailureOrigin.Introduced)
                            .Select(attr => $"test={DiagnosticValue(attr.TestIdentity)} origin={attr.Origin} " +
                                $"attribution-evidence=\"{DiagnosticValue(attr.Evidence)}\" rerun={RerunDiagnostic(attr.CandidateRerun)}")));
                var attribution = summary.CheckAttributions?.Where(attr => attr.CheckName == check.Name).ToArray();
                if (attribution is not { Length: 1 } || attribution[0].Origin != AcceptanceFailureOrigin.Introduced ||
                    attribution[0].Cause != AcceptanceFailureCause.NotClassified)
                    return Refuse(SoloHoldReasons.AuthoritativeAttributionMismatch);
                candidateChecks.Add((item, ReadRows(item, receipt.ResultsRoot, allowConfirmedFlakes: true)));
            }

            var dropped = SoloAcceptanceFlakePartition.GetDroppedIdentities(candidateChecks.Select(candidate => candidate.Partition));
            foreach (var (item, partition) in candidateChecks)
            {
                var check = item.Check;
                var rows = partition.ReadFailures(dropped);
                foreach (var identity in check.FailingTestIdentities!.Where(identity => !dropped.Contains(identity)))
                {
                    var bindings = proof.Bindings.Where(binding => binding.CheckName == check.Name && binding.TestIdentity == identity).ToArray();
                    if (bindings.Length != 1) return Refuse(SoloHoldReasons.TestBindingMismatch);
                    var baselineChecks = proof.BaselineChecks.Where(b => b.Check.Name == bindings[0].BaselineCheckName).ToArray();
                    if (baselineChecks.Length != 1) return Refuse(SoloHoldReasons.BaselineCheckMismatch);
                    var baseline = baselineChecks[0];
                    // Recheck the persisted authoritative arm and the retained check as well.
                    var armChecks = proof.Baseline.Checks.Where(b => b.Name == baseline.Check.Name).ToArray();
                    if (armChecks.Length != 1 || JsonSerializer.Serialize(armChecks[0]) != JsonSerializer.Serialize(baseline.Check))
                        return Refuse(SoloHoldReasons.BaselineResultMismatch);
                    var selector = AcceptanceFailureAttributionPlanner.NormalizeIdentity(identity);
                    if (baseline.Check.BaselineSourceAbsence is { } absent)
                    {
                        if (absent.BaselineSha != receipt.BaselineSha || absent.Selector != selector ||
                            baseline.Check.FailureClassification != AcceptanceFailureClassifications.FocusedSelectionAbsentAtBaseline ||
                            baseline.Check.ExecutedTestCount != 0 || baseline.Check.Passed || baseline.Artifacts.Count != 0)
                            return Refuse(SoloHoldReasons.SourceAbsenceInvalid);
                    }
                    else
                    {
                        if (!Usable(baseline.Check)) return Refuse(SoloHoldReasons.BaselineApparatusOrUnknown);
                        if (!MatchesRepairProject(check.TestProjectPath, baseline.Check.TestProjectPath))
                            return Refuse(SoloHoldReasons.BaselineResultMismatch);
                        var baselineRows = ReadRows(baseline, receipt.ResultsRoot).ValidateRows();
                        var selected = baselineRows.Where(row => AcceptanceFailureAttributionPlanner.NormalizeIdentity(row.Key) == selector).ToArray();
                        if (selected.Length == 0 || selected.Any(row => row.Key == identity && row.Value.Outcome == "Failed"))
                            return Refuse(SoloHoldReasons.BaselineInheritedOrUncovered);
                        if (baseline.Check.Passed)
                        {
                            var exact = baselineRows.Where(row => row.Key == identity).ToArray();
                            if (exact.Length != 1 || exact[0].Value.Outcome != "Passed" ||
                                baselineRows.Any(row => row.Value.Outcome != "Passed")) return Refuse(SoloHoldReasons.BaselineGreenUncovered);
                        }
                        else if (identity == selector || selected.Any(row => row.Key == AcceptanceFailureAttributionPlanner.NormalizeIdentity(row.Key)) ||
                            !selected.Any(row => row.Value.Outcome == "Failed")) return Refuse(SoloHoldReasons.DataCaseAmbiguous);
                    }
                    var rerun = check.FailingTestAttributions.Single(attr => attr.TestIdentity == identity).CandidateRerun;
                    if (rerun is not null)
                    {
                        if (rerun.Outcome != "Failed" || rerun.Error is not null ||
                            !item.CandidateReruns.TryGetValue(identity, out var artifact))
                            return Refuse(SoloHoldReasons.CandidateRerunUnconfirmed);
                        var repeated = ReadTrx(artifact, receipt.ResultsRoot).Where(row => row.Key == identity).ToArray();
                        if (repeated.Length != 1 || repeated[0].Value.Outcome != "Failed")
                            return Refuse(SoloHoldReasons.CandidateRerunUnconfirmed);
                    }
                    var failed = rows.First(row => row.Key == identity && row.Value.Outcome == "Failed");
                    var message = failed.Value.Message;
                    var first = message?.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')[0]
                        ?? "(no message)";
                    feedback[identity] = (first.Length <= 2000 ? first : first[..1997] + "...") + $" (trx: {failed.Path})";
                    rowFeedback.Add((check.Name, identity), feedback[identity]);
                }
            }
            return feedback.Count == 0 ? Refuse(SoloHoldReasons.FailingTestsAbsent) : new(
                new("solo", receipt.AttemptId, receipt.CandidateSha, feedback.Keys.ToArray(),
                    string.Join(Environment.NewLine, feedback.Select(row => $"reproduced {row.Key}: {row.Value}"))), "solo-evidence-validated")
                { DroppedFlakeIdentities = dropped.ToArray(), IntroducedRowFeedback = rowFeedback };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or XmlException or
            ArgumentException or InvalidOperationException or NullReferenceException)
        {
            return Refuse(SoloHoldReasons.EvidenceInvalid + ": " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static (SoloAcceptanceFailureReceipt? Receipt, SoloAcceptanceFailureEvidence? Proof, string Reason)
        ReadProof(Goal goal, SoloAcceptanceConsumptionContext? current, bool retainUnresolvedIdentities = false)
    {
        var summary = goal.LatestAcceptanceFailure;
        if (summary?.SoloFailureReceipt is not { } receipt) return (null, null, SoloHoldReasons.ReceiptAbsent);
        if (current is null && !retainUnresolvedIdentities) return (null, null, SoloHoldReasons.CurrentIdentityUnavailable);
        if (receipt.ContractVersion != 1 || receipt.CaptureError is not null)
            return (null, null, SoloHoldReasons.CaptureUnavailable + ": " + receipt.CaptureError);
        if (receipt.GoalId != goal.Id.Value || string.IsNullOrWhiteSpace(receipt.AttemptId) ||
            receipt.AttemptId != summary.AttemptId) return (null, null, SoloHoldReasons.GoalOrAttemptMismatch);
        if (receipt.CandidateSha != summary.BranchHeadSha || receipt.MainSha != summary.MainHeadSha ||
            new[] { receipt.CandidateSha, receipt.CandidateTreeSha, receipt.MainSha, receipt.BaselineSha }
                .Any(string.IsNullOrWhiteSpace)) return (null, null, SoloHoldReasons.RevisionMismatch);
        var stale = current is null || receipt.CandidateSha != current.CandidateSha || receipt.MainSha != current.MainSha ||
            receipt.CandidateTreeSha != current.CandidateTreeSha || receipt.BaselineSha != current.BaselineSha;
        if (stale && !retainUnresolvedIdentities) return (null, null, SoloHoldReasons.RevisionMismatch);
        var proof = JsonSerializer.Deserialize<SoloAcceptanceFailureEvidence>(ReadArtifact(receipt.Evidence, receipt.ResultsRoot))
            ?? throw new InvalidDataException("solo-proof-null");
        // The inner identity was written before the envelope digest existed.
        if (proof.Identity != receipt with { Evidence = receipt.Evidence with { Sha256 = "" } })
            return (null, null, SoloHoldReasons.ProofIdentityMismatch);
        if (proof.Checks is null || proof.BaselineChecks is null || proof.Bindings is null || proof.Baseline is null ||
            proof.Baseline.Sha != receipt.BaselineSha || proof.Baseline.Arm != FindingEvidenceArm.Baseline ||
            (!retainUnresolvedIdentities && (!proof.Baseline.Accepted || proof.Baseline.Disposition == FindingEvidenceArmDisposition.ApparatusFailure)))
            return (null, null, SoloHoldReasons.BaselineUnavailable);
        if (!SameNames(proof.Checks.Select(item => item.Check.Name), summary.FailedChecks) ||
            !SameNames(proof.BaselineChecks.Select(item => item.Check.Name), proof.Baseline.Checks.Select(check => check.Name)))
            return (null, null, SoloHoldReasons.CheckMismatch);
        return (receipt, proof, stale ? SoloHoldReasons.RevisionMismatch : "");
    }

    private static SoloAcceptanceFailureRead NotIntroduced(string predicate, AcceptanceCheckResult check, string? detail = null) =>
        Refuse($"{SoloHoldReasons.CandidateNotIntroduced}: predicate={predicate}; check={DiagnosticValue(check.Name)}; " +
            $"tests={(check.FailingTestIdentities is { Count: > 0 } identities ? DiagnosticNames(identities) : "(unavailable)")}" +
            $"; origins={(check.FailingTestAttributions is { Count: > 0 } attributions
                ? "[" + string.Join(", ", attributions.Select(attr => attr is null ? "(absent)" :
                    $"test={DiagnosticValue(attr.TestIdentity)} origin={attr.Origin}")) + "]" : "(unavailable)")}" +
            (detail is null ? "" : "; " + detail));

    private static string DiagnosticValue(string? value) => value ?? "(absent)";

    private static string DiagnosticNames(IEnumerable<string> names) =>
        "[" + string.Join(", ", names.Select(DiagnosticValue)) + "]";

    private static string RerunDiagnostic(CandidateFailureRerunEvidence? rerun) => rerun is null ? "(absent)" :
        $"{DiagnosticValue(rerun.Outcome)}|{DiagnosticValue(rerun.ReceiptPointer)}|{DiagnosticValue(rerun.Error)}";

    private static string AttributionMismatch(AcceptanceCheckResult check, IReadOnlyList<string> identities)
    {
        var attributed = check.FailingTestAttributions!.Select(attr => attr.TestIdentity).ToArray();
        return $"missing={DiagnosticNames(identities.Except(attributed, StringComparer.Ordinal).Order(StringComparer.Ordinal))}; " +
            $"unexpected={DiagnosticNames(attributed.Except(identities, StringComparer.Ordinal).Order(StringComparer.Ordinal))}; " +
            $"duplicate={DiagnosticNames(attributed.GroupBy(name => name, StringComparer.Ordinal).Where(group => group.Count() > 1)
                .Select(group => group.Key).Order(StringComparer.Ordinal))}";
    }

    private static bool Usable(AcceptanceCheckResult check) =>
        check.FailureCauseEvidence?.Cause is null or AcceptanceFailureCause.NotClassified &&
        check.FailureClassification is null or AcceptanceShardCompletionPredicates.FailingTrx &&
        check.ExecutedTestCount is > 0 && check.CompletionDecision?.TimedOut != true &&
        (check.Passed ? check.ExitCode == 0 : check.ExitCode is > 0);

    internal sealed record TrxRow(string Key, AcceptanceTrxFailure Value, string Path,
        IReadOnlyList<string>? AmbiguousIdentities = null);

    private static SoloAcceptanceFlakePartition ReadRows(SoloAcceptanceCheckEvidence check, string root,
        bool allowConfirmedFlakes = false, bool partitionAmbiguousRows = false)
    {
        if (check.Artifacts is not { Count: > 0 }) throw new InvalidDataException("solo-trx-absent");
        var artifactRows = check.Artifacts.Select(artifact => ReadTrx(artifact, root, partitionAmbiguousRows)).ToArray();
        return SoloAcceptanceFlakePartition.Classify(check.Check, artifactRows, allowConfirmedFlakes, partitionAmbiguousRows);
    }

    private static List<TrxRow> ReadTrx(SoloAcceptanceArtifact artifact, string root, bool partitionAmbiguousRows = false)
    {
        using var stream = new MemoryStream(ReadArtifact(artifact, root), writable: false);
        var doc = XDocument.Load(stream);
        if (doc.Root?.Name.LocalName != "TestRun" || string.IsNullOrWhiteSpace(doc.Root.Attribute("id")?.Value))
            throw new InvalidDataException("solo-trx-run-identity-missing: identity=(unresolved); outcomes=(unavailable)");
        var definitions = new Dictionary<string, XElement>(StringComparer.OrdinalIgnoreCase);
        var alternateDefinitions = new Dictionary<string, List<XElement>>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in doc.Descendants().Where(e => e.Name.LocalName == "UnitTest"))
        {
            if (definition.Attribute("id")?.Value is { Length: > 0 } id)
            {
                if (definitions.TryAdd(id, definition)) continue;
                if (partitionAmbiguousRows)
                {
                    if (!alternateDefinitions.TryGetValue(id, out var alternatives))
                        alternateDefinitions.Add(id, alternatives = []);
                    alternatives.Add(definition);
                    continue;
                }
            }
            throw new InvalidDataException($"solo-trx-ambiguous-definition: identity=(unresolved); outcomes=(unavailable); testId={definition.Attribute("id")?.Value ?? "(absent)"}");
        }
        var rows = new List<TrxRow>();
        foreach (var row in doc.Descendants().Where(e => e.Name.LocalName == "UnitTestResult"))
        {
            var testId = row.Attribute("testId")?.Value ?? "";
            if (!definitions.TryGetValue(testId, out var definition))
                throw new InvalidDataException("solo-trx-definition-missing: " + RowContext(row, null));
            var identity = AcceptanceTrxTestIdentityResolver.Resolve(row, definition);
            var outcome = row.Attribute("outcome")?.Value;
            if (string.IsNullOrWhiteSpace(identity) || outcome is not ("Passed" or "Failed" or "NotExecuted"))
                throw new InvalidDataException("solo-trx-nonexecuted-row: " + RowContext(row, identity));
            var ambiguous = alternateDefinitions.TryGetValue(testId, out var alternatives)
                ? alternatives.Select(other => AcceptanceTrxTestIdentityResolver.Resolve(row, other))
                    .OfType<string>().Prepend(identity).Distinct(StringComparer.Ordinal).ToArray()
                : null;
            rows.Add(new(identity, new(identity, outcome, null, null), artifact.Path, ambiguous));
        }
        if (rows.Count == 0) throw new InvalidDataException("solo-trx-empty: identity=(unavailable); outcomes=(unavailable)");
        // Prove uniqueness before looking up a failure message by identity.
        if (!partitionAmbiguousRows) RequireUnambiguousFailures(rows, "solo-trx-ambiguous-row");
        var failures = AcceptanceTrxFailureReader.Read(doc, artifact.Path).Failures
            .ToLookup(failure => failure.TestName, StringComparer.Ordinal);
        return rows.Select(row => row.AmbiguousIdentities is null && row.Value.Outcome == "Failed"
            ? row with { Value = failures[row.Key].First(failure => failure.Outcome == "Failed") } : row).ToList();
    }

    private static void RequireUnambiguousFailures(IEnumerable<TrxRow> rows, string reason)
    {
        foreach (var group in rows.GroupBy(row => row.Key, StringComparer.Ordinal))
            if (group.Count() > 1 && group.Any(row => row.Value.Outcome == "Failed"))
                throw new InvalidDataException($"{reason}: identity={group.Key}; outcomes={string.Join(",", group.Select(row => row.Value.Outcome))}");
    }

    private static string RowContext(XElement row, string? identity) =>
        $"identity={(string.IsNullOrWhiteSpace(identity) ? "(unresolved)" : identity)}; outcomes={row.Attribute("outcome")?.Value ?? "(absent)"}; " +
        $"testId={row.Attribute("testId")?.Value ?? "(absent)"}; testName={row.Attribute("testName")?.Value ?? "(absent)"}";

    private static byte[] ReadArtifact(SoloAcceptanceArtifact artifact, string root)
    {
        var prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!Path.GetFullPath(artifact.Path).StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("solo-foreign-artifact");
        var bytes = File.ReadAllBytes(artifact.Path);
        if (SoloAcceptanceFailureEvidenceWriter.Digest(bytes) != artifact.Sha256) throw new InvalidDataException("solo-artifact-digest-mismatch");
        return bytes;
    }

    internal static bool SameNames(IEnumerable<string> left, IEnumerable<string> right)
    {
        var a = left.Order(StringComparer.Ordinal).ToArray();
        var b = right.Order(StringComparer.Ordinal).ToArray();
        return a.Distinct(StringComparer.Ordinal).Count() == a.Length && a.SequenceEqual(b, StringComparer.Ordinal);
    }
    private static SoloAcceptanceFailureRead Refuse(string reason) => new(null, reason);
}
