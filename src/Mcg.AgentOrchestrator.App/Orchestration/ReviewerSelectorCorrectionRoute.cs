using System.Text.Json;
using System.Text.Json.Nodes;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ReviewerSelectorCorrectionRoute
{
    private const string ClosurePrefix = "finding-evidence batch-closed; batch_key=";
    private static readonly JsonSerializerOptions ArtifactJson = new(JsonSerializerDefaults.Web);

    private sealed record AffectedFinding(string FindingId, string RequestIdentity);
    private sealed record SelectorFailure(string AttemptId, FocusedEvidenceRejectionCode Code, string Diagnostic);
    private sealed record Correction(string RoundFingerprint, AffectedFinding[] Findings, SelectorFailure[] Failures);
    private sealed record ClosedAccounting(Correction? SelectorCorrection);

    internal static FindingRoundBatchCoordinator.TerminalRefusal AttributeRefusal(
        ConductorDriver driver, Goal goal, TaskSpec requester, FindingEvidenceBatch batch,
        ConductorParallelAcceptanceAttempt attempt, string candidate, AcceptanceGateEngineSettings? settings,
        FindingRoundBatchCoordinator.TerminalRefusal refusal)
    {
        if (requester.RequiredRole != AgentRole.Reviewer || batch.Members.Count < 2 ||
            refusal.Reason != FindingEvidenceNotHonouredReason.UnparseableSelection ||
            attempt is not { Outcome: ConductorParallelAcceptanceAttemptOutcome.Failed, CompletedAt: not null, SupersededBy: null } ||
            !string.Equals(attempt.BranchHeadSha, candidate, StringComparison.OrdinalIgnoreCase) ||
            attempt.FocusedEvidenceRequest != batch.Request ||
            ReadRejection(attempt) is not { Code: FocusedEvidenceRejectionCode.UnresolvableSelection } rejection)
            return refusal;

        // The resolver stops at the first rejected selection. Requests without that selection
        // remain pending so their own execution can determine their evidence outcome.
        var token = rejection.OffendingToken.Trim();
        var matchingSelections = batch.TypedRequest.Selections.Where(selection => selection.TestClass == token).ToArray();
        var rejectedSelection = matchingSelections.Length == 1 ? matchingSelections[0] :
            ResolveRejectedProject(driver, goal, refusal.Repository, settings, matchingSelections, rejection);
        if (rejectedSelection is null) return refusal;

        var members = batch.Members.Where(member =>
                FindingEvidenceRoundPlanner.ExpandSelections(member.TypedRequest.Selections).Contains(rejectedSelection))
            .Select(member => new FindingRoundBatchAccounting.Member(member.Identity,
                member.Findings.Select(finding => finding.StableId).ToArray())).ToArray();
        return members.Length == 0 ? refusal : refusal with { Members = members, RejectedSelection = rejectedSelection };
    }

    private static FindingEvidenceSelection? ResolveRejectedProject(ConductorDriver driver, Goal goal, string? repository,
        AcceptanceGateEngineSettings? settings, FindingEvidenceSelection[] matchingSelections, FocusedEvidenceRejection rejection)
    {
        if (matchingSelections.Length == 0 || settings is null) return null;
        var root = FindingEvidenceRequestNormalization.IsHomeRepository(repository)
            ? HomeWorktree(driver, goal) : driver.ResolveKeyedEvidenceTarget(goal, repository!).Target?.WorktreePath;
        if (root is null) return null;

        return matchingSelections.FirstOrDefault(selection => !GoalAcceptanceVerifier.TryBuildFocusedEvidenceChecks(
                ConductorDriver.FormatFindingEvidenceSelection(selection), settings, root, out _, out _, out var failure) &&
            failure.Code == rejection.Code && failure.OffendingToken.Trim() == rejection.OffendingToken.Trim());
    }

    private static string? HomeWorktree(ConductorDriver driver, Goal goal) => driver.ExecutionDirectory is { } directory
        ? GoalWorktrees.TryResolve(directory, goal.Id) : null;

    internal static FailedGoalFindingObservation ApplyToSettlement(ConductorDriver driver, Goal goal, TaskSpec requester,
        FindingRoundBatchCoordinator.ExecutionContext execution, FailedGoalFindingObservation decision)
    {
        if (decision.Kind != FailedGoalFindingObservationKind.FindingOperatorEvidenceRequired ||
            requester.RequiredRole != AgentRole.Reviewer || decision.BatchClosure is not { } closure ||
            closure.Identity.RequestingTaskId != requester.Id) return decision;

        var identity = closure.Identity;
        var unavailable = FindingRoundBatchAccounting.RequiredUnavailable(goal, requester, identity);
        if (unavailable.Length == 0) return decision;
        var history = ReadCorrections(goal, requester.Id).ToArray();
        if (history.Any(item => item.RoundFingerprint == identity.RoundFingerprint))
            return FailedGoalFindingObservation.Observed(FailedGoalFindingObservationKind.FindingEvidencePending,
                "This Reviewer submission already received selector correction.");

        var required = FindingRoundBatchAccounting.RequiredFindingIds(goal, requester);
        var affectedIds = unavailable.SelectMany(item => item.FindingIds).Where(required.Contains).ToHashSet(StringComparer.Ordinal);
        if (history.SelectMany(item => item.Findings).Any(prior => affectedIds.Contains(prior.FindingId))) return decision;

        var affected = execution.Round.Findings.Where(item => affectedIds.Contains(item.StableId) && item.EvidenceRequest is not null)
            .Select(item => new AffectedFinding(item.StableId, ConductorDriver.BuildFindingEvidenceIdentity(item.EvidenceRequest!)))
            .OrderBy(item => item.FindingId, StringComparer.Ordinal).ToArray();
        if (affected.Length != affectedIds.Count) return decision;

        var attempts = unavailable.Any(item => item.ReceiptId is not null)
            ? driver.FocusedEvidenceAttemptCoordinator.ReadFocusedCoverageAttempts(goal.Id) : [];
        var failures = new Dictionary<string, SelectorFailure>(StringComparer.Ordinal);
        foreach (var item in unavailable)
        {
            if (item.ReceiptId is null || ReadFailure(driver, goal, identity, execution, item, attempts) is not { } failure)
                return decision;
            failures.TryAdd(failure.AttemptId, failure);
        }

        var correction = new Correction(identity.RoundFingerprint, affected, failures.Values.ToArray());
        // The delivery prefix keeps this mechanical return in the same Reviewer round.
        var feedback = "finding evidence-on-demand: Correct and resubmit the focused evidence selector. " +
            string.Join(" ", correction.Failures.Select(item => item.Diagnostic));
        var summary = FindingRoundBatchAccounting.Summarize(goal, requester, identity, $"Reviewer: {feedback}");
        var accounting = JsonSerializer.SerializeToNode(summary)!.AsObject();
        accounting[nameof(ClosedAccounting.SelectorCorrection)] = JsonSerializer.SerializeToNode(correction);
        return FailedGoalFindingObservation.Routed(FailedGoalFindingObservationKind.FindingEvidenceDeliveryRecorded,
            requester.Id, ConductorDriver.BuildFailedGoalAttemptIdentity(requester), feedback, null)
            with { BatchClosure = new(identity, accounting.ToJsonString()) };
    }

    private static SelectorFailure? ReadFailure(ConductorDriver driver, Goal goal, FindingEvidenceBatchIdentity identity,
        FindingRoundBatchCoordinator.ExecutionContext execution, FindingRoundBatchAccounting.Observation unavailable,
        IReadOnlyList<ConductorParallelAcceptanceAttempt> attempts)
    {
        const string prefix = FindingRoundBatchCoordinator.FailedAttemptRunPrefix;
        foreach (var run in goal.Timeline.Where(item => item.TaskId == identity.RequestingTaskId &&
                     item.Kind == ProgressKind.FindingEvidenceRunRecorded && item.Message.StartsWith(prefix, StringComparison.Ordinal)))
        {
            if (!run.Message.Contains($"; receipt_id={unavailable.ReceiptId};", StringComparison.Ordinal)) continue;
            var end = run.Message.IndexOf(';', prefix.Length);
            if (end <= prefix.Length) continue;
            var attemptId = run.Message[prefix.Length..end];
            var attempt = attempts.SingleOrDefault(item => item.AttemptId == attemptId);
            var dispositions = attempt?.FocusedEvidenceRequestDispositions ?? [];
            if (attempt is not { Outcome: ConductorParallelAcceptanceAttemptOutcome.Failed, CompletedAt: not null,
                    SupersededBy: null } || attempt.FindingRoundFingerprint != identity.RoundFingerprint ||
                !dispositions.Any(item =>
                    item.Disposition.StartsWith("executed-", StringComparison.Ordinal) &&
                    item.RequestIdentity == unavailable.Identity && unavailable.FindingIds.Contains(item.FindingStableId)))
                continue;

            KeyedRepositoryEvidenceTarget? target = null;
            if (attempt.FocusedEvidenceRepository is { } key &&
                (!execution.Targets.TryGetValue(key, out var resolution) || (target = resolution.Target) is null)) continue;
            var candidate = target?.CandidateHead ?? identity.CandidateSha;
            var basis = target is null ? execution.HomeBasis : FindingRoundBatchCoordinator.ExecutionContext.TargetBasis(target);
            if (!string.Equals(attempt.BranchHeadSha, candidate, StringComparison.OrdinalIgnoreCase) ||
                attempt.FocusedExecutionBasisIdentity != basis ||
                !run.Message.Contains($"; execution_basis={basis};", StringComparison.Ordinal)) continue;
            if (ReadRejection(attempt) is not { Code: FocusedEvidenceRejectionCode.UnresolvableSelection } rejection)
                return null;

            var rejectedSelection = FindingRoundBatchCoordinator.ReadTerminalRefusal(run.Message, identity)?.RejectedSelection;
            if (rejectedSelection is null && dispositions.Any(item => item.Disposition == "executed-batched")) return null;

            var root = target?.WorktreePath ?? HomeWorktree(driver, goal);
            var request = rejectedSelection is null ? attempt.FocusedEvidenceRequest! :
                ConductorDriver.FormatFindingEvidenceSelection(rejectedSelection);
            var suggestions = root is null ? string.Empty : FocusedEvidenceBareSelectorResolution.DescribeFilenameMatches(
                root, request, target?.EngineSettings ?? execution.HomeSettings, rejection.OffendingToken);
            return new(attemptId, rejection.Code, rejection.Detail + $"; offending_filter='{rejection.OffendingToken}'." + suggestions);
        }
        return null;
    }

    private static FocusedEvidenceRejection? ReadRejection(ConductorParallelAcceptanceAttempt attempt)
    {
        try
        {
            var path = Path.Combine(Path.GetDirectoryName(attempt.MetadataPath)!, attempt.AttemptId + ".result.json");
            var text = File.ReadAllText(path);
            if (attempt.ReplayResultFingerprint is { } fingerprint && FindingEvidenceReviewContext.Hash(text) != fingerprint)
                return null;
            var artifact = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(text, ArtifactJson);
            return artifact is { Kind: "focused-evidence", FocusedEvidence: { Accepted: false } result } &&
                string.Equals(artifact.BranchHeadSha, attempt.BranchHeadSha, StringComparison.OrdinalIgnoreCase) &&
                result.Request == attempt.FocusedEvidenceRequest ? result.Rejection : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static IEnumerable<Correction> ReadCorrections(Goal goal, TaskId requester)
    {
        foreach (var item in goal.Timeline.Where(item => item.TaskId == requester &&
                     item.Kind == ProgressKind.FindingEvidenceRequestRecorded && item.Message.StartsWith(ClosurePrefix, StringComparison.Ordinal)))
        {
            var start = item.Message.IndexOf('{', ClosurePrefix.Length);
            if (start >= 0 && JsonSerializer.Deserialize<ClosedAccounting>(item.Message[start..])?.SelectorCorrection is { } correction)
                yield return correction;
        }
    }
}
