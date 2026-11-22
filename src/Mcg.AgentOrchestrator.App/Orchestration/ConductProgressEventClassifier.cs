namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class ConductProgressEventClassifier
{
    internal static bool TryClassify(string line, out string kind, out string? goalId)
    {
        goalId = TryExtractToken(line, "goal=");
        var head = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        kind = head switch
        {
            "ACCEPTANCE" => "acceptance",
            "ACCEPTANCE_LEASE_ACQUIRE" => "acceptance-lease",
            "ACCEPTANCE_LEASE_HANDOFF" => "acceptance-lease",
            "ACCEPTANCE_LEASE_PERMIT_RELEASE" => "acceptance-lease",
            "ACCEPTANCE_LEASE_RELEASE" => "acceptance-lease",
            "ACCEPTANCE_LEASE_YIELD" or "ACCEPTANCE_LEASE_DEGRADE" => "acceptance-lease",
            "BUILD_LOCK_BLOCKED" => "lock-blocker",
            "INVALIDATED_ATTEMPT_STOP" => "invalidated-attempt-stop",
            // Named from the emitter so the operator-visible token and its classification cannot drift.
            ConductorUnappliedExitWatch.EventName => "exit-unapplied",
            "GOAL" => ClassifyGoalEvent(line),
            "GOAL_STALLED" => "goal-stalled",
            "GOAL_LEFT_WORKING_SET" => "goal-left-working-set",
            "LOCK" => "lock-blocker",
            "LOOP_HANDOFF" => "loop-handoff",
            "LOOP_HANDOFF_FAILED" => "loop-handoff",
            "LOOP_HANDOFF_PENDING" => "loop-handoff",
            "LOOP_HANDOFF_SKIPPED" => "loop-handoff",
            "LOOP_RELAUNCH_SCHEDULED" or "LOOP_RELAUNCH_HELD" => "loop-relaunch",
            "LOOP_RELAUNCH_NOT_REQUIRED" => "loop-relaunch",
            "LOOP_RELAUNCH_DRAIN" or "LOOP_RELAUNCH_DETACH" => "loop-relaunch",
            "LOOP_RELAUNCH_REBUILD" => "loop-relaunch",
            "LOOP_RELAUNCH_ROLLBACK" => "loop-relaunch-rollback",
            "LOOP_JANITORIAL_FAILED" => "loop-janitorial-failure",
            "LOOP_JANITORIAL_DEGRADED" => "loop-janitorial-degraded",
            "LOOP_JANITORIAL_RECOVERED" => "loop-janitorial-recovered",
            "LOOP_JANITORIAL_RETRYING" => "loop-janitorial-retry",
            "LOOP_JANITORIAL_RETRY_SUCCEEDED" => "loop-janitorial-retry",
            "LOOP_START" => "loop-start", "STARTUP_FIRST_TICK" => "startup-first-tick",
            "LOOP_START_DEFERRED" => "loop-start-deferred",
            "LOOP_STOP_DEFERRED" => "loop-stop-deferred",
            "LOOP_DRAIN" => "loop-drain",
            "LOOP_DRAIN_EVIDENCE" => "loop-drain-evidence",
            "LOOP_DRAINED" => "loop-drained",
            "LOOP_STOP" => "loop-stop", "POLICY_RELOAD" => "policy-reload",
            "PHASE_TIMING" => "phase-timing",
            "TICK_LOAD_COUNTERS" => "tick-load-counters",
            "POLICY_RELOAD_FAILED" => "policy-reload-failed", "POLICY_INVALID" => "policy-invalid",
            "POLICY_WARNING" => "policy-warning",
            "SPECULATIVE_COHORT_PLAN" => "speculative-cohort-plan", "PRIORITY_ADMISSION" or "PRIORITY_STARVATION_GUARD" or "PRIORITY_DRIFT" or "PRIORITY_DILUTION" or "PRIORITY_OVERLAP" => "priority",
            "TRAIN_RECEIPT_STALE" => "train-receipt-stale",
            "TRAIN_RECEIPT_REVALIDATE" => "train-receipt-revalidate",
            "TRAIN_RECEIPT_HELD" => "train-receipt-held",
            "TRAIN_RECEIPT_RELEASED" => "train-receipt-released",
            "ACCEPTANCE_COHORT" or "COHORT_APPROVAL_OVERLAP" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_ENTRY" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_EXIT" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_INFLIGHT" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_FAIRNESS" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_FAIRNESS_TRANSITION" => "acceptance-cohort",
            "ACCEPTANCE_COHORT_ATTRIBUTION_VERDICT" => "acceptance-cohort",
            "SWEEP_BLOCKER" => "sweep-blocker", "SWEEP_OWNED_ROOT_DEFERRED" => "sweep-owned-root-deferred",
            "SWEEP_ESCALATION" => "sweep-escalation",
            "SWEEP_REMEDY_ATTEMPT" => "sweep-remedy-attempt",
            "SWEEP_REMEDY_RESULT" => "sweep-remedy-result", "SWEEP_OWNED_ROOT_OBSERVED" => "sweep-owned-root-observed",
            "SWEEP_GOAL_ROOT_RECLAIMED" => "sweep-goal-root-reclaimed", "SWEEP_BUILD_ROOT_RETENTION" => "sweep-build-root-retention",
            "SWEEP_GOAL_ROOT_RECLAIM_FAILED" => "sweep-goal-root-reclaim-failed", "SWEEP_UNSTORED_BUILD_ROOT_RETENTION" => "sweep-unstored-build-root-retention",
            "SET_ASIDE_SELF_CLEARED" => "set-aside-self-cleared",
            "BLOCKED_RECHECK_HEARTBEAT" => "blocked-recheck-heartbeat",
            "TICK_WRITE_BUSY" => "lock-blocker", "TICK_GIT_CENSUS" => "tick-git-census",
            "TICK_WRITE_DEGRADED" => "lock-blocker",
            "GLANCE" => "progressive-review-glance",
            "WATCH_TRANSITION" => "watch-transition", "SELECTED_EXECUTION_BACKSTOP" => "selected-execution-backstop", MainFullVerificationCoordinator.Event or MainFullVerificationCoordinator.HoldEvent => "main-full-verification",
            "PROMPT_ROLLOUT_SUSPECT" => "prompt-rollout-suspect", "CONSOLE_CODE_PAGE_CHANGED" => "console-code-page-changed",
            _ => string.Empty
        };

        return kind.Length > 0;
    }

    private static string ClassifyGoalEvent(string line)
    {
        if (line.Contains("result=done", StringComparison.Ordinal) ||
            line.Contains("result=landed", StringComparison.Ordinal))
            return "goal-landing";
        if (line.Contains("result=escalated", StringComparison.Ordinal) ||
            line.Contains("escalated", StringComparison.Ordinal))
            return "goal-escalation";
        if (line.Contains("result=", StringComparison.Ordinal))
            return "goal";
        return string.Empty;
    }

    private static string? TryExtractToken(string line, string prefix)
    {
        var start = line.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
            return null;

        start += prefix.Length;
        var end = line.IndexOf(' ', start);
        return end < 0 ? line[start..] : line[start..end];
    }
}
