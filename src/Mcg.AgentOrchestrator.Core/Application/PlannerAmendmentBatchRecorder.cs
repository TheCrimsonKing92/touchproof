namespace Mcg.AgentOrchestrator.Core;

internal static class PlannerAmendmentBatchRecorder
{
    internal static bool TryHandleDispatch(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        HumanInputDirectiveParseResult batch, TaskVerificationRecord verification, int completedRound)
    {
        if (batch.IsMalformed)
        {
            PlannerEvidenceCompletion.Reject(kernel, goal, task, verification, batch.Diagnostic!);
            return true;
        }
        return batch.Directives.Count > 1 && batch.Directives.All(item => item.PlannerEvidence is null) &&
            Record(kernel, goal, task, batch.Directives,
                verification.AuthoritativeStandardOutput ?? verification.StandardOutput,
                verification.WorkerResultPresent, completedRound, verification.StandardOutputPath);
    }

    // The parser validates the entire batch before either execution path records any request.
    internal static bool Record(AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        IReadOnlyList<HumanInputDirective> directives, string output, bool workerResultPresent,
        int completedRound, string? workerResultReference)
    {
        var hasBlocker = WorkerResultBlockers.TryFindBlocker(output, out var blocker);
        var hold = workerResultPresent;
        foreach (var directive in directives)
        {
            var question = directive.Question;
            if (workerResultPresent && hasBlocker)
                question += $"{Environment.NewLine}Accompanying WORKER_RESULT blocker evidence: {blocker}";
            var result = kernel.RequestHumanInputDeduplicated(goal.Id, task.Id, question,
                kind: directive.Kind, questionFingerprint: directive.QuestionFingerprint,
                blockerFingerprint: directive.BlockerFingerprint,
                completedRound: workerResultPresent ? completedRound : null,
                workerResultLogReference: workerResultReference,
                recordDuplicateSuppression: workerResultPresent,
                amendmentCriterionIndex: directive.AmendmentCriterionIndex);
            if (workerResultPresent && result.WasSuppressedByAnswer &&
                WorkerResultBlockers.TryGetBlockersStatus(output, out var status) &&
                status == WorkerResultBlockers.BlockersStatus.None)
                kernel.RecordHumanInputWorkerResultContradiction(goal.Id, task.Id, result.Request.Id,
                    completedRound, workerResultReference);
            hold |= !result.WasSuppressedByAnswer;
        }

        // An answered replay can restore Assigned after an open sibling held the task.
        // Reconcile only after recording every sibling, retaining suppression failures.
        var pending = kernel.GetPendingBlockingHumanInput(goal.Id).FirstOrDefault(item => item.TaskId == task.Id);
        if (pending is not null)
        {
            if (task.Status is not (WorkTaskStatus.WaitingForHuman or WorkTaskStatus.Failed))
                kernel.HoldTaskForExistingHumanInput(goal, task.Id, pending.Kind);
            return true;
        }
        return hold;
    }
}
