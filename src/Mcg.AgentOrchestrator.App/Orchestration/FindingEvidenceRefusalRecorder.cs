using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class FindingEvidenceRefusalRecorder
{
    internal static void Record(
        GoalId goalId,
        TaskSpec requestingTask,
        ReviewFinding finding,
        FindingEvidenceNotHonouredReason reason,
        string detail,
        string candidateSha, AcceptanceGateEngineSettings? inventorySettings,
        Action<GoalId, TaskId, IReadOnlyList<FindingEvidenceRequestDisposition>> recordDispositions,
        Action<GoalId, TaskId, string, FindingEvidenceOutcome, FindingEvidenceReceipt?> recordOutcome,
        Action<GoalId, TaskId, string> recordRequest, Func<string, string> trim)
    {
        if (WorkerResultBlockers.TryFindReviewFindingRound(requestingTask.LastVerification, out var round, out _))
        {
            var hasCandidate = ConductorGitRevisionReader.IsValid(candidateSha);
            recordDispositions(goalId, requestingTask.Id,
                [new(finding.StableId, ConductorDriver.BuildFindingEvidenceIdentity(finding.EvidenceRequest!), "not-honoured",
                    FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(reason),
                    hasCandidate ? FindingEvidenceRequestDecision.Refused : FindingEvidenceRequestDecision.CandidateUnavailable,
                    hasCandidate ? candidateSha : null, ConductorDriver.BuildFindingRoundFingerprint(requestingTask, round))]);
        }
        recordOutcome(
            goalId, requestingTask.Id, finding.StableId,
            FocusedEvidenceInventoryIdentity.NotHonoured(reason, detail, inventorySettings), null);
        recordRequest(
            goalId, requestingTask.Id,
            $"finding-evidence disposition=not-honoured; role={requestingTask.RequiredRole}; task_id={requestingTask.Id}; " +
            $"finding_id={finding.StableId}; candidate_sha={candidateSha}; receipt_id=none; " +
            $"reason={FindingEvidenceNotHonouredReasonJsonConverter.ToWireValue(reason)}; detail={trim(detail)}");
    }

}
