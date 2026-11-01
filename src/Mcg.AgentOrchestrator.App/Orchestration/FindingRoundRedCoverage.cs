using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Proof of per-test red coverage, scoped to an uninterrupted candidate in one finding round.
// Rebuilt from durable receipts/events so a conductor restart does not change the decision.
internal static class FindingRoundRedCoverage
{
    internal const string Reason = "redundant-candidate-red";

    internal static FailedGoalFindingObservation Complete(
        ConductorDriver driver, Goal goal, TaskSpec task, IReadOnlyList<FindingEvidenceBatch> pending,
        FindingEvidenceBatch runnable, FindingRoundBatchPlan plan, FindingEvidenceReceipt receipt,
        string candidateSha, string fingerprint, string basis,
        Action<GoalId, TaskId, IReadOnlyList<FindingEvidenceRequestDisposition>> recordDispositions,
        Action<GoalId, TaskId, string, FindingEvidenceOutcome, FindingEvidenceReceipt?> recordOutcome,
        Action<GoalId, TaskId, string> recordRequest)
    {
        var coverage = Evaluate(pending, task, goal.Timeline, candidateSha, fingerprint, basis, receipt);
        coverage.RecordSkips(goal, task, candidateSha, fingerprint, basis, recordDispositions, recordOutcome, recordRequest);
        return coverage.Remaining.Count > 0
            ? FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                "Focused evidence completed; another distinct request from the same finding round remains pending: " +
                $"next_request={coverage.Remaining[0].Identity}; completed_receipt_id={receipt.ReceiptId}.")
            : driver.BuildReceiptClosureOrDeliveryRetry(goal, task, candidateSha,
                runnable.Findings.Concat(plan.CoveredFindings).Concat(coverage.Findings).DistinctBy(finding => finding.StableId).ToArray(),
                plan.CoveredReceiptIds.Concat(coverage.ReceiptIds).Append(receipt.ReceiptId).Distinct(StringComparer.Ordinal).ToArray(),
                "Focused evidence completed and its receipt was attached to the requesting finding." +
                (plan.RedCovered.Count == 0 ? "" : " " + plan.CoveredEvidence) + "; " + coverage.Evidence);
    }

    internal static Coverage Evaluate(
        IReadOnlyList<FindingEvidenceBatch> batches, TaskSpec task, IReadOnlyList<ProgressEvent> timeline,
        string candidateSha, string fingerprint, string basis, FindingEvidenceReceipt? completed = null)
    {
        var boundary = -1;
        for (var index = 0; index < timeline.Count; index++)
        {
            var match = Regex.Match(timeline[index].Message, @"\bcandidate_sha=([^;\s]+)");
            if (match.Success && !string.Equals(match.Groups[1].Value, candidateSha, StringComparison.OrdinalIgnoreCase))
                boundary = index;
        }
        var runIds = timeline.Skip(boundary + 1)
            .Where(item => item.TaskId == task.Id && item.Kind == ProgressKind.FindingEvidenceRunRecorded)
            .Select(item => Regex.Match(item.Message, @"\breceipt_id=([^;\s]+)"))
            .Where(match => match.Success).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        var receipts = task.VerificationHistory.SelectMany(record => record.FindingEvidenceReceipts ?? [])
            .Concat(completed is null ? [] : new[] { completed }).GroupBy(receipt => receipt.ReceiptId)
            .Select(group => group.Last()).Where(receipt =>
                (runIds.Contains(receipt.ReceiptId) || ReferenceEquals(receipt, completed)) &&
                receipt is { Accepted: true, Passed: false } && Plain(receipt.Request) &&
                receipt.NegativeControlOutcome is null && receipt.RevertPathsRejection is null &&
                string.Equals(receipt.CandidateSha, candidateSha, StringComparison.OrdinalIgnoreCase) &&
                receipt.FindingRoundFingerprint == fingerprint && receipt.ExecutionBasisIdentity == basis &&
                (receipt.Arms ?? []).Count(arm => arm.Arm == FindingEvidenceArm.Candidate) == 1 &&
                !(receipt.Arms ?? []).Any(arm => arm.Disposition is FindingEvidenceArmDisposition.ApparatusFailure
                    or FindingEvidenceArmDisposition.Inconclusive))
            .Where(receipt => receipt.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate) is
                { Accepted: true, Passed: false, Disposition: FindingEvidenceArmDisposition.Red,
                    ExecutedTestCount: > 0, FailingTestIdentities.Count: > 0 } arm &&
                string.Equals(arm.Sha, candidateSha, StringComparison.OrdinalIgnoreCase)).ToArray();
        var evidence = receipts.Select(ReadExecution).Where(item => item is not null).Cast<Execution>().ToArray();
        var skipped = new List<(FindingEvidenceBatch Batch, FindingEvidenceReceipt[] Receipts)>();
        var remaining = new List<FindingEvidenceBatch>();
        foreach (var batch in batches)
        {
            var sources = new List<FindingEvidenceReceipt>();
            var selections = FindingEvidenceRoundPlanner.ExpandSelections(batch.TypedRequest.Selections);
            var covered = Plain(batch.TypedRequest) && selections.Count > 0;
            foreach (var selection in selections)
            {
                if (!FindingEvidenceFilterSyntax.TryGetExpandableClass(selection.TestClass, out var token, out _))
                {
                    covered = false;
                    break;
                }
                // A broader executed selector proves completeness; a partial list of method results cannot
                // prove the inventory of an unexecuted class. Use the executor's FQN contains semantics.
                var matching = evidence.Where(item => item.Selections.Any(executed =>
                    executed.TestProject == selection.TestProject &&
                    FindingEvidenceFilterSyntax.TryGetExpandableClass(executed.TestClass, out var prior, out _) &&
                    token.Contains(prior, StringComparison.Ordinal))).ToArray();
                var rows = matching.SelectMany(item => item.Outcomes.Where(pair =>
                    MatchesToken(pair.Key, token))).ToArray();
                if (rows.Length == 0 || rows.Any(pair => !pair.Value))
                {
                    covered = false;
                    break;
                }
                sources.AddRange(matching.Where(item => item.Outcomes.Keys.Any(identity =>
                    MatchesToken(identity, token))).Select(item => item.Receipt));
            }
            if (covered) skipped.Add((batch, sources.DistinctBy(receipt => receipt.ReceiptId).ToArray()));
            else remaining.Add(batch);
        }
        return new(remaining, skipped, receipts, boundary + 1);
    }

    private static bool Plain(FindingEvidenceRequest request) => request.NegativeControl is null &&
        request.RevertPaths is not { Count: > 0 } && request.Mutation is null;

    private static bool MatchesToken(string identity, string token) =>
        AcceptanceTrxTestIdentityResolver.NormalizeSelector(identity).Contains(token, StringComparison.Ordinal);

    private static Execution? ReadExecution(FindingEvidenceReceipt receipt)
    {
        var arm = receipt.Arms!.Single(item => item.Arm == FindingEvidenceArm.Candidate);
        var selections = FindingEvidenceExecutedCoverage.ExecutedSelections(receipt,
            selection => selection.TestProject + ":" + selection.TestClass);
        // TRX has no project identity. Ambiguous multi-project executions cannot prove per-project coverage.
        if (selections.Select(selection => selection.TestProject).Distinct().Count() != 1 ||
            arm.TestResultPaths is not { Count: > 0 } paths || arm.ReceiptArtifacts is not { Count: > 0 } artifacts ||
            !AcceptanceCohortGateEvidence.HasCoherentTrxEvidence(paths)) return null;
        try
        {
            var trx = artifacts.Where(artifact => artifact.Kind == "trx").ToArray();
            if (trx.Length != paths.Count || !trx.Select(artifact => artifact.Path).Order(StringComparer.OrdinalIgnoreCase)
                    .SequenceEqual(paths.Select(Path.GetFullPath).Order(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase))
                return null;
            var outcomes = new Dictionary<string, bool>(StringComparer.Ordinal);
            var executed = 0;
            foreach (var artifact in trx)
            {
                using var stream = File.Open(artifact.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (artifact.Length <= 0 || stream.Length != artifact.Length ||
                    !Convert.ToHexStringLower(SHA256.HashData(stream)).Equals(artifact.Sha256, StringComparison.Ordinal))
                    return null;
                stream.Position = 0;
                var document = XDocument.Load(stream);
                var definitions = document.Descendants().Where(item => item.Name.LocalName == "UnitTest")
                    .ToDictionary(item => (string?)item.Attribute("id") ?? "", StringComparer.Ordinal);
                foreach (var result in document.Descendants().Where(item => item.Name.LocalName == "UnitTestResult"))
                {
                    definitions.TryGetValue((string?)result.Attribute("testId") ?? "", out var definition);
                    var identity = AcceptanceTrxTestIdentityResolver.Resolve(result, definition);
                    if (identity is null) return null;
                    var outcome = (string?)result.Attribute("outcome");
                    if (outcome is "Passed" or "Failed") executed++;
                    outcomes[identity] = outcome == "Failed" && (!outcomes.TryGetValue(identity, out var red) || red);
                }
            }
            // Require agreement between the arm's failures and the content-bound execution identities.
            if (executed != arm.ExecutedTestCount || outcomes.Count == 0 ||
                outcomes.Any(pair => pair.Value && !arm.FailingTestIdentities!.Contains(pair.Key, StringComparer.Ordinal)))
                return null;
            return new(receipt, selections, outcomes);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException or
                                   ArgumentException or NotSupportedException or OverflowException)
        {
            return null;
        }
    }

    internal sealed record Coverage(
        IReadOnlyList<FindingEvidenceBatch> Remaining,
        IReadOnlyList<(FindingEvidenceBatch Batch, FindingEvidenceReceipt[] Receipts)> Skipped,
        IReadOnlyList<FindingEvidenceReceipt> RedReceipts, int WindowStart)
    {
        internal ReviewFinding[] Findings => Skipped.SelectMany(item => item.Batch.Findings)
            .DistinctBy(finding => finding.StableId).ToArray();
        internal string[] ReceiptIds => RedReceipts.Select(receipt => receipt.ReceiptId).ToArray();
        internal string Evidence => "failing_tests=" + string.Join(',', RedReceipts.SelectMany(receipt =>
            receipt.Arms!.Single(arm => arm.Arm == FindingEvidenceArm.Candidate).FailingTestIdentities!)
            .Distinct(StringComparer.Ordinal));

        internal void RecordSkips(Goal goal, TaskSpec task, string candidateSha, string fingerprint, string basis,
            Action<GoalId, TaskId, IReadOnlyList<FindingEvidenceRequestDisposition>> recordDispositions,
            Action<GoalId, TaskId, string, FindingEvidenceOutcome, FindingEvidenceReceipt?> recordOutcome,
            Action<GoalId, TaskId, string> recordRequest)
        {
            foreach (var (batch, receipts) in Skipped)
            foreach (var member in batch.Members)
            foreach (var finding in member.Findings)
            {
                var ids = receipts.Select(receipt => receipt.ReceiptId).ToArray();
                var marker = $"finding-evidence disposition=covered-by-receipt; reason={Reason}; " +
                    $"finding_id={finding.StableId}; candidate_sha={candidateSha}; " +
                    $"finding_round={fingerprint}; candidate_epoch={WindowStart}; request_identity={member.Identity}; source_receipt_ids={string.Join(',', ids)};";
                if (goal.Timeline.Any(item => item.TaskId == task.Id &&
                    item.Kind == ProgressKind.FindingEvidenceRequestRecorded && item.Message == marker)) continue;
                var outcome = new FindingEvidenceOutcome(true, ids[0], Detail: Evidence,
                    ResultReason: FindingEvidenceOutcomeReason.CandidateRed, RequestedSelectionIdentity: member.Identity,
                    DecisionReason: Reason, SourceReceiptIds: ids);
                foreach (var receipt in receipts) recordOutcome(goal.Id, task.Id, finding.StableId, outcome, receipt);
                recordDispositions(goal.Id, task.Id, [new(finding.StableId, member.Identity, "covered-by-receipt", Reason,
                    FindingEvidenceRequestDecision.CoveredByReceipt, candidateSha, fingerprint,
                    SourceReceiptIds: ids, ExecutionBasisIdentity: basis)]);
                recordRequest(goal.Id, task.Id, marker);
            }
        }
    }

    private sealed record Execution(FindingEvidenceReceipt Receipt,
        IReadOnlyList<FindingEvidenceSelection> Selections, IReadOnlyDictionary<string, bool> Outcomes);
}
