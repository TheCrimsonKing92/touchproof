using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.Rendering;
using Mcg.AgentOrchestrator.App.SubscriptionPlanning;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Cli;

internal static partial class CliCommandHandlers
{
private static bool RunAcceptanceWorkspaceMerge(CliExecutionContext context, bool skipVerify = false)
{
    var goal = context.CurrentGoal!;
    if (CliMultiRepositoryAcceptance.Apply(context, goal, runGate: !skipVerify)) return false;
    var owner = $"acceptance:{Environment.ProcessId}:{Guid.NewGuid():N}";
    var lease = new ReconcileSweepRemediationStore(context.Workspace.SqliteStatePath)
        .TryAcquireAcceptanceLease(goal.Id.Value, owner, TimeSpan.FromMinutes(30));
    if (lease is null)
    {
        Console.WriteLine($"BLOCKER step=acceptance-claim goal={goal.Id.Value[..8]} reason=already-running action=\"Wait for the active acceptance operation to finish.\"");
        return false;
    }
    using (lease)
    {
        return RunAcceptanceWorkspaceMergeCore(context, skipVerify);
    }
}

internal static bool RunAcceptanceWorkspaceMergeCore(CliExecutionContext context, bool skipVerify = false)
{
    var goal = context.CurrentGoal!;
    if (CliMultiRepositoryAcceptance.Apply(context, goal, runGate: !skipVerify)) return false;
    var root = context.Workspace.ExecutionDirectory;
    var pendingFastForward = GoalWorktrees.ReadCliFastForward(root, goal.Id);
    IReadOnlyList<string> landingChangedFiles = [];
    // The goal transaction may have committed before the Git receipt was acknowledged.
    if (goal.Status == GoalStatus.Completed && pendingFastForward is { FromAcceptance: true })
    {
        var pending = CliWorkspaceMerge.FinishFastForward(context, goal, pendingFastForward, null, out _);
        if (!pending.FastForwarded) return ReportCheckoutPending(pending.Message);
        CompletePublishedAcceptance(pending);
        return false;
    }

    if (TryReconcileLandedCleanedAcceptance(context, goal, "acceptance retry", out var reconciledDetail))
    {
        Console.WriteLine(reconciledDetail);
        context.EventWriter.AppendAcceptanceResult(goal.Id, true, []);
        AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=passed detail=already-landed-cleaned");
        return true;
    }

    if (TryNormalizePrematureCompletedGoalForAcceptance(context, goal, out var normalizedGoal, out var normalizedDetail))
    {
        Console.WriteLine(normalizedDetail);
        goal = normalizedGoal;
        context.CurrentGoal = normalizedGoal;
    }

    CliWorkspaceMerge.RetainRepairEvidence(context, goal);
    if (goal.Status == GoalStatus.AcceptanceFailed)
    {
        var failedWorktreePath = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id);
        var failedBranchHead = TryResolveGitHead(context, failedWorktreePath);
        var failedMainHead = TryResolveGitHead(context, context.Workspace.ExecutionDirectory);
        if (CliWorkspaceMerge.ClearSupersededAcceptanceFailureForCandidate(
            context,
            goal,
            failedBranchHead,
            failedMainHead))
        {
            goal = context.Kernel.GetGoal(goal.Id);
            context.CurrentGoal = goal;
            Console.WriteLine(
                $"Acceptance history: superseded failure is historical for candidate " +
                $"{FormatAcceptanceCandidate(failedBranchHead, failedMainHead)}.");
        }
    }

    if (goal.Status != GoalStatus.Verified) return false;

    var engineHealth = PostLandingCanaryFactory.CreateCircuit(context.Workspace).Read();
    var engineDecision = AcceptanceEngineAcceptanceGate.Decide(
        engineHealth.Health,
        AcceptanceEngineAcceptanceGate.DefaultUnavailablePolicy);
    if (!engineDecision.Allowed)
    {
        Console.WriteLine(
            $"BLOCKER step=acceptance-engine reason=\"{engineDecision.Reason}\" " +
            $"landing={engineHealth.LandingSha ?? "unknown"} failure={engineHealth.FailureReason ?? "pending"} " +
            "action=\"Repair the acceptance engine, then run acceptance-engine clear <note>.\"");
        return false;
    }

    var worktreePath = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id);
    AcceptanceVerificationResult? verification = null;
    string? testedWorktreeHead = null;
    GateReadyCandidateRevisionPair? preparedPair = null;
    DateTimeOffset? acceptanceAttemptStartedAt = null;
    AcceptanceMergeGuardSnapshot? expectedMergeGuard = null;
    var reuseNormalGate = false;
    string? recoveredNormalGate = null;
    var testedMainHead = TryResolveGitHead(context, context.Workspace.ExecutionDirectory);
    if (pendingFastForward is not null)
    {
        if (pendingFastForward.FromAcceptance &&
            CliWorkspaceMerge.MatchesFastForwardCandidate(context, goal, worktreePath, pendingFastForward))
        {
            testedWorktreeHead = pendingFastForward.PromotedRevision;
            testedMainHead = pendingFastForward.PriorRevision;
            return FinalizeMerge(pendingFastForward);
        }

        // Recover the old publication and checkout before evaluating any new pair.
        var pending = CliWorkspaceMerge.FinishFastForward(context, goal, pendingFastForward, null, out _);
        if (!pending.FastForwarded) return ReportCheckoutPending(pending.Message);
        CliWorkspaceMerge.RunPostLandingCanary(context, goal, pending.ChangedFiles!, pendingFastForward);
        GoalWorktrees.AcknowledgeCliFastForward(root, goal.Id, pendingFastForward);
    }
    if (worktreePath is not null)
    {
        var rebaseCheckStarted = System.Diagnostics.Stopwatch.StartNew();
        var needsRebase = context.Worktrees.NeedsRebaseOntoMain(context.Workspace.ExecutionDirectory, goal.Id);
        rebaseCheckStarted.Stop();
        context.PhaseTimings.Record(
            "workspace-rebase-check",
            rebaseCheckStarted.Elapsed,
            ("goal", goal.Id.Value[..8]),
            ("needed", needsRebase));

        if (needsRebase)
        {
            var rebaseStarted = System.Diagnostics.Stopwatch.StartNew();
            var rebase = context.Worktrees.TryRebaseOntoMain(context.Workspace.ExecutionDirectory, goal.Id);
            rebaseStarted.Stop();
            context.PhaseTimings.Record(
                "workspace-rebase",
                rebaseStarted.Elapsed,
                ("goal", goal.Id.Value[..8]),
                ("status", rebase.Status),
                ("updated", rebase.UpdatedBranch));
            Console.WriteLine($"Workspace rebase: {FormatWorkspaceRebase(rebase)}");

            if (!rebase.UpdatedBranch)
            {
                var failedChecks = new[] { $"workspace rebase: {rebase.Status.ToString().ToLowerInvariant()}" };
                testedWorktreeHead = TryResolveGitHead(context, worktreePath);
                testedMainHead = TryResolveGitHead(context, context.Workspace.ExecutionDirectory);
                context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks, testedWorktreeHead, testedMainHead);
                GoalOperationJournal.AcceptanceFailed(
                    context.Workspace.ExecutionDirectory,
                    goal,
                    "acceptance",
                    testedWorktreeHead,
                    testedMainHead,
                    $"Acceptance failed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {string.Join(", ", failedChecks)}.");
                context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
                AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=failed stage=rebase checks={FormatConductEventChecks(failedChecks)}");
                Console.WriteLine("Acceptance evidence: blocked; merge blocked");
                return false;
            }
        }
        else
        {
            context.PhaseTimings.Record(
                "workspace-rebase",
                TimeSpan.Zero,
                ("goal", goal.Id.Value[..8]),
                ("status", "skipped"),
                ("reason", "already-up-to-date"));
        }

        testedWorktreeHead = context.Worktrees.ResolveHead(worktreePath);
        testedMainHead = TryResolveGitHead(context, context.Workspace.ExecutionDirectory);
        preparedPair = new(testedWorktreeHead, testedMainHead);
        if (CliWorkspaceMerge.ReportRepairHold(context, goal, preparedPair, worktreePath, skipVerify, out var admissionStore)) return false;
        reuseNormalGate = CliWorkspaceMerge.HasPassedNormalGate(root, admissionStore, goal, preparedPair);
        if (!skipVerify && !reuseNormalGate && admissionStore is not null)
        {
            recoveredNormalGate = CliWorkspaceMerge.FindRecoverableNormalGate(root, goal, preparedPair)?.IdempotencyKey;
            reuseNormalGate = recoveredNormalGate is not null;
        }
        if (reuseNormalGate && !skipVerify) Console.WriteLine("Verification: reused the recorded normal acceptance pass for the current revision pair.");

        if (CliWorkspaceMerge.ClearSupersededAcceptanceFailureForCandidate(context, goal, testedWorktreeHead, testedMainHead))
        {
            goal = context.Kernel.GetGoal(goal.Id);
            context.CurrentGoal = goal;
            Console.WriteLine($"Acceptance history: superseded failure is historical for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}.");
        }

        var proposedMergeGuard = AcceptanceMergeGuard.Capture(context.Kernel, goal.Id);
        var preflightGuard = context.PrepareAcceptanceMergeGuard(new AcceptanceMergeGuardPreflightRequest(
            goal.Id,
            proposedMergeGuard,
            testedWorktreeHead,
            testedMainHead));
        expectedMergeGuard = preflightGuard.CurrentGuard;
        if (preflightGuard.GuardAbort is { } preflightMismatch)
        {
            var preflightAbort = new AcceptanceMergeCommitResult(
                false,
                AcceptanceMergeGuard.BuildAbortMessage(goal.Id, preflightMismatch, passingGateReceiptRecorded: false),
                preflightMismatch);
            RecordAcceptanceGuardAbort(
                context,
                goal,
                preflightAbort,
                testedWorktreeHead,
                testedMainHead,
                acceptanceAttemptStartedAt,
                verification: null,
                stage: "preflight-state-guard");
            return false;
        }

        var changedFiles = context.Worktrees.GetChangedFiles(worktreePath);
        landingChangedFiles = changedFiles;
        acceptanceAttemptStartedAt = DateTimeOffset.UtcNow;
        if (skipVerify)
        {
            context.PhaseTimings.Record(
                "verification-suite",
                TimeSpan.Zero,
                ("goal", goal.Id.Value[..8]),
                ("status", "skipped"),
                ("reason", "--skip-verify"));
            Console.WriteLine("Verification: skipped (--skip-verify)");
        }
        else if (!reuseNormalGate)
        {
            var sourceSizePreflight = SourceSizeRatchetPreflight.Evaluate(worktreePath);
            if (sourceSizePreflight.HasBlockingViolation)
            {
                IReadOnlyList<string> failedChecks =
                [
                    SourceSizeRatchetPreflight.CheckName,
                    .. SourceSizeRatchetPreflight.BlockingViolationMessages(sourceSizePreflight)
                ];
                context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks, testedWorktreeHead, testedMainHead);
                GoalOperationJournal.AcceptanceFailed(
                    context.Workspace.ExecutionDirectory,
                    goal,
                    "acceptance",
                    testedWorktreeHead,
                    testedMainHead,
                    $"Acceptance failed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {sourceSizePreflight.Message}.");
                context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
                AppendConductEvent(
                    context,
                    "acceptance",
                    goal.Id,
                    $"ACCEPTANCE goal={goal.Id.Value[..8]} result=failed stage=source-size-preflight checks={FormatConductEventChecks(failedChecks)}");
                Console.WriteLine($"Acceptance verification: {sourceSizePreflight.Message}");
                Console.WriteLine("Acceptance evidence: blocked; merge blocked");
                return false;
            }

            var verificationStarted = System.Diagnostics.Stopwatch.StartNew();
            int? stableSlotIndex = null;
            DotnetBuildEnvironmentLease? stableSlotLease = null;
            try
            {
                (stableSlotLease, stableSlotIndex) = CliAcceptanceBuildPermit.Acquire(
                    goal.Id,
                    context.StableSlotSelector,
                    context.StableSlotAcquisitionTimeout,
                    context.CleanupHooks.BuildStorageRoot,
                    context.Workspace.ExecutionDirectory);
            }
            catch (DotnetBuildSlotsBusyException ex)
            {
                stableSlotLease?.Dispose();
                Console.WriteLine($"SLOTS_BUSY goal={goal.Id.Value[..8]} wantedBy={ex.SlotsBusy.WantedBy} busySlots={CliAcceptanceBuildPermit.FormatBusySlots(ex.SlotsBusy.BusySlots)}");
                Console.WriteLine("Acceptance verification: stable dotnet build slots busy; goal remains ready and will retry on a later conduct tick.");
                GoalOperationJournal.AcceptanceBlocked(
                    context.Workspace.ExecutionDirectory,
                    goal,
                    "acceptance",
                    "slot-unavailable",
                    testedWorktreeHead,
                    testedMainHead,
                    $"Acceptance blocked:slot-unavailable for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}.",
                    acceptanceAttemptStartedAt);
                return false;
            }
            catch (IOException ex)
            {
                stableSlotLease?.Dispose();
                Console.WriteLine($"BLOCKER step=verification reason=build-slot-timeout detail=\"{EscapeBlockerDetail(ex.Message)}\" action=\"Wait for a stable dotnet build slot to clear, then rerun acceptance.\"");
                GoalOperationJournal.AcceptanceBlocked(
                    context.Workspace.ExecutionDirectory,
                    goal,
                    "acceptance",
                    "slot-unavailable",
                    testedWorktreeHead,
                    testedMainHead,
                    $"Acceptance blocked:slot-unavailable for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {ex.Message}",
                    acceptanceAttemptStartedAt);
                throw new InvalidOperationException("acceptance blocked waiting for a stable dotnet build slot.", ex);
            }
            using (stableSlotLease)
            {
                if (CliWorkspaceMerge.ReportRepairHold(context, goal, preparedPair, worktreePath, false, out _)) return false;

                var enteredVerifying = context.Kernel.BeginGoalAcceptanceVerification(
                    goal.Id,
                    "Acceptance entered Verifying while the terminal gate runs.");
                try
                {
                    var executionOptions = new AcceptanceRunExecutionOptions(
                        IntegrationBranch: context.Workspace.IntegrationBranch,
                        ProjectHomeDirectory: context.Workspace.ProjectHomeDirectoryOrNull,
                        ProgressSink: progress => AppendConductEvent(
                            context,
                            "gate-progress",
                            goal.Id,
                            FormatGateProgressConductEvent(progress)),
                        RemoteLaneEventSink: detail => AppendConductEvent(context, "remote-lane", goal.Id, detail),
                        RunId: Environment.GetEnvironmentVariable(
                            AcceptanceAttemptArtifactCustody.AttemptIdVariable),
                        ResultsPrefix: Environment.GetEnvironmentVariable(
                            GoalAcceptanceVerifier.AcceptanceAttemptTrxPrefixVariable),
                        LivenessCheckHint: Environment.GetEnvironmentVariable(
                            AcceptanceAttemptArtifactCustody.LivenessCheckHintVariable));
                    var executionOwner = AcceptanceExecutionOwners.CreateAttempt(
                        worktreePath,
                        goal.Id,
                        stableSlotIndex,
                        CancellationToken.None,
                        executionOptions);
                    using var qos = HostedVerifierRunQos.Begin(context.Workspace);
                    verification = AcceptanceExecutionOwnerLifetime.Run(
                        executionOwner,
                        () => context.AcceptanceVerifier.RunOwnedAsync(
                            worktreePath,
                            goal.Id,
                            changedFiles,
                            stableSlotIndex,
                            stableSlotLease,
                            executionOwner).GetAwaiter().GetResult());
                }
                catch (AcceptanceInfrastructureDeferredException ex)
                {
                    var deferredLine =
                        $"ACCEPTANCE_INFRASTRUCTURE_DEFERRED goal={goal.Id.Value[..8]} " +
                        $"reason={ex.ReasonCode} detail=\"{EscapeBlockerDetail(ex.Message)}\"";
                    Console.WriteLine(deferredLine);
                    AppendConductEvent(context, "infrastructure-deferral", goal.Id, deferredLine);
                    Console.WriteLine("Acceptance verification: trusted baseline infrastructure unavailable; goal remains ready and will retry on a later conduct tick.");
                    GoalOperationJournal.AcceptanceBlocked(
                        context.Workspace.ExecutionDirectory,
                        goal,
                        "acceptance",
                        $"INFRASTRUCTURE_DEFERRED:{ex.ReasonCode}",
                        testedWorktreeHead,
                        testedMainHead,
                        $"Acceptance blocked:INFRASTRUCTURE_DEFERRED:{ex.ReasonCode} for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {ex.Message}",
                        acceptanceAttemptStartedAt);
                    return false;
                }
                catch (BuildLockBlockedException ex)
                {
                    var blockedLine = $"BUILD_LOCK_BLOCKED goal={goal.Id.Value[..8]} {FormatBuildLockBlocked(ex.Attribution)}";
                    Console.WriteLine(blockedLine);
                    AppendConductEvent(context, "lock-blocker", goal.Id, blockedLine);
                    Console.WriteLine("Acceptance verification: build artifact lock blocked progress; goal remains ready and will retry on a later conduct tick.");
                    GoalOperationJournal.AcceptanceBlocked(
                        context.Workspace.ExecutionDirectory,
                        goal,
                        "acceptance",
                        "BUILD_LOCK_BLOCKED",
                        testedWorktreeHead,
                        testedMainHead,
                        $"Acceptance blocked:BUILD_LOCK_BLOCKED for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {FormatBuildLockBlocked(ex.Attribution)}",
                        acceptanceAttemptStartedAt);
                    return false;
                }
                finally
                {
                    if (enteredVerifying && context.Kernel.GetGoal(goal.Id).Status == GoalStatus.Verifying)
                    {
                        context.Kernel.ReconcileGoalAcceptanceVerified(
                            goal.Id,
                            "Acceptance terminal gate finished; returned to Verified for deterministic landing.");
                    }
                }
            }
            verificationStarted.Stop();
            context.PhaseTimings.Record(
                "verification-suite",
                verificationStarted.Elapsed,
                ("goal", goal.Id.Value[..8]),
                ("slot", stableSlotIndex.HasValue ? $"slot-{stableSlotIndex.Value}" : null),
                ("passed", verification.Passed),
                ("exit", verification.ExitCode),
                ("checks", verification.Checks?.Count ?? 0));
            foreach (var check in verification.Checks ?? [])
            {
                context.PhaseTimings.Record(
                    "verification-check",
                    TimeSpan.FromMilliseconds(check.DurationMilliseconds ?? 0),
                    ("goal", goal.Id.Value[..8]),
                    ("name", check.Name),
                    ("passed", check.Passed),
                    ("exit", check.ExitCode),
                    ("advisory", check.Advisory));
                Console.WriteLine($"Verification check: {(check.Passed ? "passed" : "failed")} - {check.Name}" +
                    (check.ExitCode is null ? "" : $" (exit {check.ExitCode})") +
                    (string.IsNullOrWhiteSpace(check.ArtifactsPath) ? "" : $" artifacts={check.ArtifactsPath}"));
            }
        }
    }
    else
    {
        context.PhaseTimings.Record(
            "workspace-rebase",
            TimeSpan.Zero,
            ("goal", goal.Id.Value[..8]),
            ("status", "skipped"),
            ("reason", "no-worktree"));
        context.PhaseTimings.Record(
            "verification-suite",
            TimeSpan.Zero,
            ("goal", goal.Id.Value[..8]),
            ("status", "skipped"),
            ("reason", "no-worktree"));
        expectedMergeGuard = AcceptanceMergeGuard.Capture(context.Kernel, goal.Id);
    }

    if (verification is { Passed: true } || skipVerify)
    {
        context.Kernel.ClearAcceptanceFailure(goal.Id);
    }

    var evidence = context.Worktrees.BuildAcceptanceEvidence(
        context.Kernel,
        goal,
        worktreePath,
        verification,
        skipVerify,
        context.Workspace.ExecutionDirectory,
        verificationAlreadyPassed: reuseNormalGate);
    AcceptanceEvidenceBundleTextView.Print(evidence);

    if (!evidence.Passed)
    {
        var failedChecks = verification?.Checks?
            .Where(c => !c.Passed && !c.Advisory)
            .Select(c => c.Name)
            .ToList();
        if (failedChecks is null or { Count: 0 })
        {
            failedChecks = ["acceptance evidence blocked"];
        }
        var retainSoloFailure = verification is { RequiresSoloFailureReceipt: true } or { SoloFailureReceipt: not null };
        if (retainSoloFailure)
            CliWorkspaceMerge.RetainVerificationFailure(context, goal, failedChecks, testedWorktreeHead, testedMainHead, verification);

        if (verification is { Passed: false })
        {
            Console.WriteLine($"Verification: failed (exit {verification.ExitCode}); merge blocked");
            Console.WriteLine($"Verification artifacts: {verification.ArtifactsPath}");
            var timeoutBlocker = TryBuildVerificationTimeoutBlocker(verification);
            if (timeoutBlocker is not null)
            {
                Console.WriteLine(timeoutBlocker);
            }

            if (!string.IsNullOrWhiteSpace(verification.OutputTail))
            {
                Console.WriteLine(verification.OutputTail);
            }

            if (timeoutBlocker is not null)
            {
                var timedOutChecks = verification.Checks?
                    .Where(IsBlockingTimeoutCheck)
                    .Select(c => c.Name)
                    .ToList() ?? ["acceptance-check-timeout"];
                GoalOperationJournal.AcceptanceBlocked(
                    context.Workspace.ExecutionDirectory,
                    goal,
                    "acceptance",
                    "timeout",
                    testedWorktreeHead,
                    testedMainHead,
                    $"Acceptance blocked:timeout for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {string.Join(", ", timedOutChecks)}.",
                    acceptanceAttemptStartedAt,
                    GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
                context.EventWriter.AppendAcceptanceResult(goal.Id, false, timedOutChecks);
                AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=blocked type=timeout checks={FormatConductEventChecks(timedOutChecks)}");
                return false;
            }
        }
        else
        {
            Console.WriteLine("Acceptance evidence: blocked; merge blocked");
        }

        if (!retainSoloFailure)
            CliWorkspaceMerge.RetainVerificationFailure(context, goal, failedChecks, testedWorktreeHead, testedMainHead, verification);
        GoalOperationJournal.AcceptanceFailed(
            context.Workspace.ExecutionDirectory,
            goal,
            "acceptance",
            testedWorktreeHead,
            testedMainHead,
            $"Acceptance failed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {string.Join(", ", failedChecks)}.",
            acceptanceAttemptStartedAt,
            GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
        AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=failed checks={FormatConductEventChecks(failedChecks)}");
        return false;
    }

    if (verification is { Passed: true, Skipped: false })
    {
        CliWorkspaceMerge.RecordValidatedGatePass(context, goal, testedWorktreeHead, testedMainHead,
            $"Acceptance gate passed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}; merge pending.",
            acceptanceAttemptStartedAt, verification);
    }

    if (verification is not null)
    {
        Console.WriteLine($"Verification: passed (exit {verification.ExitCode})");
        Console.WriteLine($"Verification artifacts: {verification.ArtifactsPath}");
    }

    var normalGateAcknowledgement = verification is { Passed: true, Skipped: false } passedGate
        ? passedGate.AttemptId ?? "inline-gate"
        : recoveredNormalGate;
    if (normalGateAcknowledgement is not null)
    {
        if (CliWorkspaceMerge.ReportRepairHold(context, goal, preparedPair, worktreePath, false, out var repairStore)) return false;
        if (repairStore is not null)
            repairStore.AcknowledgeEffect(SoloBaselineRepairStore.NormalGateKey(goal.Id.Value, testedWorktreeHead!, testedMainHead!),
                normalGateAcknowledgement);
    }

    var hostStop = context.StopAcceptanceHosts(new AcceptanceHostStopRequest(
        goal.Id,
        worktreePath,
        TimeSpan.FromSeconds(30)));
    Console.WriteLine(hostStop.Message);
    if (!hostStop.Succeeded)
    {
        context.Kernel.RecordAcceptanceFailure(goal.Id, ["stop-host"], testedWorktreeHead, testedMainHead);
        GoalOperationJournal.AcceptanceFailed(
            context.Workspace.ExecutionDirectory,
            goal,
            "acceptance",
            testedWorktreeHead,
            testedMainHead,
            $"Acceptance failed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: stop-host.",
            acceptanceAttemptStartedAt,
            GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
        context.EventWriter.AppendAcceptanceResult(goal.Id, false, ["stop-host"]);
        AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=failed checks=stop-host");
        return false;
    }

    var preMergeObligations = context.Kernel.GetGoal(goal.Id)
        .GetLandingPrerequisiteObligations(testedWorktreeHead);
    if (preMergeObligations.Count > 0)
    {
        Console.WriteLine(
            "Acceptance evidence: merge blocked by outstanding criterion evidence: " +
            string.Join(", ", preMergeObligations.Select(item => $"{item.DisplayLabel}:{item.Owner}:{item.State}")));
        return false;
    }

    return FinalizeMerge();

bool ReportCheckoutPending(string message)
{
    Console.WriteLine($"BLOCKER step=checkout-sync reason=\"{EscapeBlockerDetail(message)}\" action=\"Resolve the checkout issue in the repository root, then rerun acceptance.\"");
    return false;
}

bool FinalizeMerge(CliFastForwardReceipt? recovery = null)
{
    var (mergeCommit, merge) = CliWorkspaceMerge.FinalizeAcceptance(context, goal, worktreePath, testedWorktreeHead,
        expectedMergeGuard, verification is { Passed: true } || reuseNormalGate, preparedPair, recovery);
    if (mergeCommit.GuardAborted)
    {
        RecordAcceptanceGuardAbort(
            context,
            goal,
            mergeCommit,
            testedWorktreeHead,
            testedMainHead,
            acceptanceAttemptStartedAt,
            verification,
            stage: "state-guard");
        return false;
    }

    if (mergeCommit.CheckoutPending)
        return ReportCheckoutPending(mergeCommit.Message!);

    if (mergeCommit.Message is not null)
    {
        Console.WriteLine($"Workspace merge: {mergeCommit.Message}");
        if (mergeCommit.FastForwarded)
        {
            CompletePublishedAcceptance(merge, testedWorktreeHead, testedMainHead, acceptanceAttemptStartedAt, verification);
        }
        else
        {
            var failedChecks = new[] { "merge" };
            context.Kernel.RecordAcceptanceFailure(goal.Id, failedChecks, testedWorktreeHead, testedMainHead);
            GoalOperationJournal.AcceptanceFailed(
                context.Workspace.ExecutionDirectory,
                goal,
                "acceptance",
                testedWorktreeHead,
                testedMainHead,
                $"Acceptance failed for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: merge.",
                acceptanceAttemptStartedAt,
                GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
            context.EventWriter.AppendAcceptanceResult(goal.Id, false, failedChecks);
            AppendConductEvent(context, "acceptance", goal.Id, $"ACCEPTANCE goal={goal.Id.Value[..8]} result=failed stage=merge checks=merge");
            Console.WriteLine($"BLOCKER step=merge reason={mergeCommit.Message} action=\"Resolve conflicts on {context.Worktrees.BranchName(goal.Id)}, rerun verification, then rerun acceptance.\"");
        }
        return mergeCommit.FastForwarded;
    }

    CompletePublishedAcceptance(merge, testedWorktreeHead, testedMainHead, acceptanceAttemptStartedAt, verification);
    return true;
}

void CompletePublishedAcceptance(GoalWorktreeMergeResult? merge, string? branchHead = null,
    string? mainHead = null, DateTimeOffset? startedAt = null, AcceptanceVerificationResult? result = null)
{
    var receipt = merge?.FastForwardReceipt;
    RecordAcceptanceCompleted(context, goal, receipt?.PromotedRevision ?? branchHead,
        receipt?.PriorRevision ?? mainHead, startedAt, result, receipt);
    CliWorkspaceMerge.RunPostLandingCanary(context, goal, merge?.ChangedFiles ?? landingChangedFiles, receipt);
    if (receipt is not null)
        GoalWorktrees.AcknowledgeCliFastForward(root, goal.Id, receipt);
}
}

private static bool TryNormalizePrematureCompletedGoalForAcceptance(
    CliExecutionContext context,
    Goal goal,
    out Goal normalizedGoal,
    out string detail)
{
    normalizedGoal = goal;
    var goalPrefix = goal.Id.Value[..8];
    if (goal.Status != GoalStatus.Completed)
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} is {goal.Status}.";
        return false;
    }

    if (!GoalWorktrees.IsGitWorkTree(context.Workspace.ExecutionDirectory))
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} is not in a git worktree.";
        return false;
    }

    var gitFacts = GoalGitFactIndex.Build(context.Workspace.ExecutionDirectory, context.Workspace.IntegrationBranch).BuildGoalBranchFacts(goal);
    var hasBranchArtifact = context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null ||
        gitFacts.HasGoalBranch;
    if (!hasBranchArtifact || gitFacts.BranchAlreadyLanded)
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} has no unmerged goal branch.";
        return false;
    }

    if (!context.Kernel.NormalizePrematureCompletedGoalForAcceptance(
            goal.Id,
            "acceptance: normalized raw Completed goal with unmerged branch back to Verified before merge."))
    {
        detail = $"acceptance normalization skipped: goal {goalPrefix} does not have passed task verification gates.";
        return false;
    }

    normalizedGoal = context.Kernel.GetGoal(goal.Id);
    detail = $"Acceptance repair: normalized raw Completed goal {goalPrefix} to Verified so acceptance can merge {context.Worktrees.BranchName(goal.Id)}.";
    return true;
}

private static void RecordAcceptanceCompleted(
    CliExecutionContext context,
    Goal goal,
    string? branchHeadSha,
    string? mainHeadSha,
    DateTimeOffset? acceptanceAttemptStartedAt,
    AcceptanceVerificationResult? verification,
    CliFastForwardReceipt? receipt)
{
    var root = context.Workspace.ExecutionDirectory;
    var journal = receipt is null ? null : GoalOperationJournal.Read(root, goal.Id);
    if (journal?.Entries.Any(entry => entry.Operation == "acceptance" && entry.AcceptanceOutcome == "passed" &&
            entry.HasCandidate(branchHeadSha, mainHeadSha)) != true)
    {
        GoalOperationJournal.AcceptancePassed(root, goal, "acceptance", branchHeadSha, mainHeadSha,
            $"Acceptance passed for candidate {FormatAcceptanceCandidate(branchHeadSha, mainHeadSha)} and merge completed.",
            acceptanceAttemptStartedAt, GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
    }
    context.Kernel.ClearAcceptanceFailure(goal.Id);
    if (goal.Status == GoalStatus.Verified)
    {
        context.Kernel.CompleteGoal(goal.Id, "Acceptance completed goal after merge; cleanup deferred to conductor sweep.");
    }

    var detail = $"ACCEPTANCE goal={goal.Id.Value[..8]} result=passed";
    if (receipt is null)
    {
        context.EventWriter.AppendAcceptanceResult(goal.Id, true, []);
        AppendConductEvent(context, "acceptance", goal.Id, detail);
        return;
    }

    var deliveryId = $"cli-acceptance-completed:{goal.Id.Value}:{receipt.PriorRevision}:{receipt.PromotedRevision}";
    if (context.EventWriter is GoalLifecycleEventWriter writer)
        writer.AppendIdempotent(goal.Id, deliveryId, target => target.AppendAcceptanceResult(goal.Id, true, []));
    else
        context.EventWriter.AppendAcceptanceResult(goal.Id, true, []);
    if (!new ConductEventLogWriter(context.Workspace.ConductEventsLogPath)
        .AppendRequired("acceptance", goal.Id.Value[..8], detail, eventId: deliveryId))
        throw new IOException($"Acceptance completion for {goal.Id.Value[..8]} could not be recorded. Rerun acceptance.");
}

private static void AppendConductEvent(CliExecutionContext context, string eventKind, GoalId goalId, string detail)
{
    try
    {
        new ConductEventLogWriter(context.Workspace.ConductEventsLogPath).Append(eventKind, goalId.Value[..8], detail);
    }
    catch
    {
        // Shared operator event streaming is advisory; command output and lifecycle events remain authoritative.
    }
}

private static string FormatGateProgressConductEvent(AcceptanceGateProgress progress) =>
    $"PHASE_PROGRESS goal={progress.GoalId?[..Math.Min(8, progress.GoalId.Length)] ?? "unknown"} phase={progress.Phase} " +
    $"elapsed_ms={(long)progress.Elapsed.TotalMilliseconds} target={FormatConductToken(progress.CurrentTarget)} " +
    $"child_pid={progress.ChildProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"} " +
    $"output_bytes={progress.OutputBytes} heartbeat={FormatConductToken(progress.HeartbeatPath)}" + progress.ShardOutcomeTokens;

private static string FormatConductToken(string value) =>
    value.IndexOfAny([' ', '\t', '\r', '\n', '"']) < 0
        ? value
        : $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

private static string FormatConductEventChecks(IEnumerable<string> checks) =>
    string.Join(",", checks.Select(check => check.Replace(' ', '_').Replace('\t', '_').Replace('\r', '_').Replace('\n', '_')));

internal static string? TryResolveGitHead(CliExecutionContext context, string? path)
{
    if (string.IsNullOrWhiteSpace(path))
        return null;

    try
    {
        return context.Worktrees is DefaultCliGoalWorktreeService
            ? GitRevisionReader.Shared.TryResolve(path, "HEAD", out var head) ? head : string.Empty
            : context.Worktrees.ResolveHead(path);
    }
    catch
    {
        return null;
    }
}

private static string FormatAcceptanceCandidate(string? branchHeadSha, string? mainHeadSha) =>
    $"branch={FormatShortSha(branchHeadSha)} main={FormatShortSha(mainHeadSha)}";

private static string FormatShortSha(string? sha) =>
    string.IsNullOrWhiteSpace(sha)
        ? "unknown"
        : sha.Trim()[..Math.Min(12, sha.Trim().Length)];

private static string? TryBuildVerificationTimeoutBlocker(AcceptanceVerificationResult verification)
{
    var timedOutCheck = verification.Checks?
        .FirstOrDefault(IsBlockingTimeoutCheck);

    if (timedOutCheck is null)
        return null;

    var artifactPath = !string.IsNullOrWhiteSpace(timedOutCheck.ArtifactsPath)
        ? timedOutCheck.ArtifactsPath
        : verification.ArtifactsPath;
    var artifactDetail = string.IsNullOrWhiteSpace(artifactPath) ? "none" : artifactPath;
    return $"BLOCKER step=verification reason=timeout check=\"{timedOutCheck.Name}\" artifacts={artifactDetail} action=\"Inspect verification command, artifact path, and last output above; rerun acceptance after clearing the blocker.\"";
}

private static bool IsBlockingTimeoutCheck(AcceptanceCheckResult check) =>
    !check.Advisory &&
    !check.Passed &&
    (check.Name.StartsWith("acceptance-check-timeout:", StringComparison.OrdinalIgnoreCase) ||
     check.ResultSummary?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true ||
     check.OutputTail?.Contains("timed out", StringComparison.OrdinalIgnoreCase) == true);

private static string FormatBuildLockBlocked(BuildLockAttribution attribution)
{
    var holders = attribution.Holders.Count == 0
        ? "unknown"
        : string.Join(
            ",",
            attribution.Holders.Select(holder =>
                $"pid-{holder.ProcessId?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown"}:{holder.ProcessName ?? "unknown"}"));
    return $"path=\"{EscapeBlockerDetail(attribution.Path)}\" holders={holders}";
}

private static string EscapeBlockerDetail(string value) =>
    value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);

private static void ReconcileLandedCleanedAcceptance(
    CliExecutionContext context,
    Goal goal,
    string source,
    bool cleanupEvidenceRecorded = false)
{
    TryReconcileLandedCleanedAcceptance(context, goal, source, out _, cleanupEvidenceRecorded);
}

private static bool TryReconcileLandedCleanedAcceptance(
    CliExecutionContext context,
    Goal goal,
    string source,
    out string detail,
    bool cleanupEvidenceRecorded = false)
{
    if (!HasLandedCleanedTerminalEvidence(context, goal, out detail, cleanupEvidenceRecorded))
    {
        return false;
    }

    if (goal.Status == GoalStatus.Verified)
    {
        context.Kernel.CompleteGoal(goal.Id, $"Acceptance repaired after durable landing and cleanup evidence from {source}.");
    }

    context.Kernel.ClearAcceptanceFailure(goal.Id);
    detail = $"Acceptance repaired: goal {goal.Id.Value[..8]} is already landed and workspace cleanup is recorded ({source}).";
    return true;
}

private static bool HasLandedCleanedTerminalEvidence(
    CliExecutionContext context,
    Goal goal,
    out string detail,
    bool cleanupEvidenceRecorded = false)
{
    var goalPrefix = goal.Id.Value[..8];
    if (goal.Status is not (GoalStatus.Verified or GoalStatus.Completed))
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} is {goal.Status}, not Verified or Completed.";
        return false;
    }

    if (context.Worktrees.TryResolve(context.Workspace.ExecutionDirectory, goal.Id) is not null)
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} still has a worktree.";
        return false;
    }

    var journal = GoalOperationJournal.Read(context.Workspace.ExecutionDirectory, goal.Id);
    var hasLandingEvidence = journal.LatestByOperation.Any(entry =>
        entry.Status == GoalOperationStatus.Completed &&
        (entry.Operation.Equals("acceptance", StringComparison.OrdinalIgnoreCase) ||
         entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase)));
    if (!hasLandingEvidence)
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} has no completed acceptance or conductor landing evidence.";
        return false;
    }

    var hasCleanupEvidence = cleanupEvidenceRecorded ||
        journal.LatestByOperation.Any(entry =>
            entry.Status == GoalOperationStatus.Completed &&
            (entry.Operation.Equals("workspace:remove", StringComparison.OrdinalIgnoreCase) ||
             entry.Operation.Equals("conductor:cleanup", StringComparison.OrdinalIgnoreCase)));
    if (!hasCleanupEvidence)
    {
        detail = $"acceptance repair blocked: goal {goalPrefix} has no completed workspace cleanup evidence.";
        return false;
    }

    detail = $"goal {goalPrefix} has completed landing and cleanup evidence.";
    return true;
}

private static void RecordAcceptanceGuardAbort(
    CliExecutionContext context,
    Goal goal,
    AcceptanceMergeCommitResult abort,
    string? testedWorktreeHead,
    string? testedMainHead,
    DateTimeOffset? acceptanceAttemptStartedAt,
    AcceptanceVerificationResult? verification,
    string stage)
{
    context.RegisterAcceptanceGuardAbort();
    var detail = abort.Message ?? "Acceptance merge aborted because a landing guard changed.";
    var field = abort.GuardAbort?.Field ?? "unknown";
    GoalOperationJournal.AcceptanceAborted(
        context.Workspace.ExecutionDirectory,
        goal,
        "acceptance",
        testedWorktreeHead,
        testedMainHead,
        $"Acceptance aborted:state-guard for candidate {FormatAcceptanceCandidate(testedWorktreeHead, testedMainHead)}: {detail}",
        acceptanceAttemptStartedAt,
        GoalOperationJournal.TryExtractBaseBuildCacheReceipt(verification));
    var abortEvent =
        $"ACCEPTANCE goal={goal.Id.Value[..8]} result=aborted stage={stage} field={FormatConductEventChecks([field])} checks={FormatConductEventChecks([detail])}";
    AppendConductEvent(
        context,
        "acceptance",
        goal.Id,
        abortEvent);
    Console.WriteLine(abortEvent);
    Console.WriteLine(
        $"BLOCKER step=acceptance-state-guard reason=aborted field=\"{EscapeBlockerDetail(field)}\" " +
        $"detail=\"{EscapeBlockerDetail(detail)}\" action=\"Quiesce conductor mutations for this goal before another landing attempt; do not use a bare retry while the goal is changing.\"");
    var projectionKernel = context.ReloadKernel([goal.Id.Value]);
    var projectionGoal = projectionKernel.Goals.FirstOrDefault(candidate => candidate.Id == goal.Id) ?? goal;
    MonitorTextView.PrintAcceptanceSummary(
        projectionGoal,
        GoalAcceptanceStatusProjector.Build(projectionKernel, projectionGoal, context.Workspace.ExecutionDirectory, context.Workspace.IntegrationBranch));
}

private static T RunGoalMarkLandedStep<T>(string stepName, Func<T> step)
{
    try
    {
        return step();
    }
    catch (Exception ex)
    {
        throw new InvalidOperationException($"goal-mark-landed {stepName} failed: {ex.Message}", ex);
    }
}

private static void PrintGoalMarkLandedSummary(
    bool hadWorktree,
    bool cleanupComplete)
{
    Console.WriteLine("Goal landed cleanup:");
    Console.WriteLine(hadWorktree
        ? cleanupComplete ? "cleanup: worktree removed" : "cleanup: worktree cleanup deferred"
        : "cleanup: worktree already absent");
    Console.WriteLine(cleanupComplete ? "cleanup: branch deleted" : "cleanup: branch cleanup deferred");
    Console.WriteLine(cleanupComplete ? "cleanup: app-host lock released" : "cleanup: app-host lock cleanup deferred");
    Console.WriteLine(cleanupComplete ? "cleanup: goal marked CleanedUp" : "cleanup: goal marked landed; cleanup-needed recorded");
}
}
