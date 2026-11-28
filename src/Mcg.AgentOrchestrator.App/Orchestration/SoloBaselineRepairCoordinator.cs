using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal interface ISoloBaselineRepairIntake
{
    GoalCreationOperatorIntentPayload PrepareRepair(string requestKey, string objective, string epicId);
    void ApplyReservedRepair(AgentOrchestratorKernel kernel, GoalCreationOperatorIntentPayload payload);
}

internal sealed class SoloBaselineRepairCoordinator(
    OrchestratorWorkspace workspace,
    AgentOrchestratorKernel kernel,
    IGoalAcceptanceVerifier verifier,
    Func<Goal, GateReadyCandidateRevisionPair?> readCurrentRevisions,
    Func<Goal, SoloAcceptanceConsumptionContext?> readContext,
    Func<SoloAcceptanceFailureHandler> failureHandler,
    Func<Goal, GoalLifecycleFacts> getFacts,
    Func<Goal, GoalLifecycleState, string, string, LandingEscalationWriteResult> writeEscalation,
    Func<string, CancellationToken, DotnetBuildEnvironmentLease> acquireStableSlot,
    Func<Goal, string, ConductorAutonomyPolicy, ConductorAdvanceResult?>? prepareAttribution = null,
    ISoloBaselineRepairIntake? repairIntake = null,
    SoloRepairCheckAttemptCoordinator? checkAttempts = null)
{
    private readonly SoloBaselineRepairStore _store = new(
        Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));

    private ISoloBaselineRepairIntake RepairIntake => repairIntake
        ?? throw new InvalidOperationException("This conductor host cannot create a baseline repair goal.");

    internal ConductorAdvanceResult? TryAdvance(Goal goal, string prefix, ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary? completion = null)
    {
        RetainSoloRepairPartition(_store, goal, () => readContext(goal), () => readCurrentRevisions(goal), completion);
        var retained = _store.ReadFindings(goal.Id.Value);
        var introducedFindings = _store.ReadFindings(goal.Id.Value, introduced: true);
        if (retained.Count == 0 && introducedFindings.Count == 0) return null;
        var current = readCurrentRevisions(goal);
        var currentIntroduced = CurrentIntroduced(_store, goal, introducedFindings, current?.BranchRevision, current?.MainRevision);
        var findings = retained.Select(finding => CheckRetainedProof(_store, goal, finding))
            .Concat(currentIntroduced.Where(finding => finding.Reason is not null)).ToArray();
        var introduced = SoloAcceptanceFailureReceiptReader.RestoreIntroduced(currentIntroduced);
        var repair = findings.Length == 0 ? null :
            ReconcileSoloRepairs(goal, prefix, policy, findings, current, allowFreshGate: introduced is null);
        introduced = ReadIntroduced(goal, current);
        if (introduced is not null && current is not null && readCurrentRevisions(goal) == current &&
            AttributedFailureDeveloperRoute.Decide(goal, introduced, current.BranchRevision, current.MainRevision) is { IsRoute: true } route)
            return failureHandler().RouteIntroduced(goal, prefix, policy, introduced, route);
        return repair;
    }

    private AttributedFailure? ReadIntroduced(Goal goal, GateReadyCandidateRevisionPair? current) =>
        SoloAcceptanceFailureReceiptReader.RestoreIntroduced(CurrentIntroduced(_store, goal,
            _store.ReadFindings(goal.Id.Value, introduced: true), current?.BranchRevision, current?.MainRevision));

    private AttributedFailure? ReadStandingIntroduced(Goal goal, string candidate)
    {
        if (goal.LatestAcceptanceFailure?.AttemptId is not { } attempt) return null;

        var findings = _store.ReadFindings(goal.Id.Value, introduced: true)
            .Where(finding => finding.Attempt == attempt && finding.Candidate == candidate)
            .Select(finding => CheckRetainedProof(_store, goal, finding));
        return SoloAcceptanceFailureReceiptReader.RestoreIntroduced(findings);
    }

    private static SoloRepairFinding[] CurrentIntroduced(SoloBaselineRepairStore store, Goal goal, IReadOnlyList<SoloRepairFinding> findings,
        string? candidate, string? main) => findings.Where(finding => finding.Candidate == candidate && finding.Main == main)
        .Select(finding => CheckRetainedProof(store, goal, finding)).ToArray();

    internal void RetainUnresolvedCompletion(Goal goal, AcceptanceVerificationSummary completion)
    {
        var findings = SoloAcceptanceFailureReceiptReader.ReadUnresolvedCompletion(completion);
        RetainFindings(_store, goal, findings);
    }

    internal static void RetainSoloRepairPartition(SoloBaselineRepairStore store, Goal goal,
        Func<SoloAcceptanceConsumptionContext?> readContext, Func<GateReadyCandidateRevisionPair?> readCurrentRevisions,
        AcceptanceVerificationSummary? completion = null)
    {
        var receipt = goal.LatestAcceptanceFailure?.SoloFailureReceipt;
        if (receipt is null && completion?.RequiresSoloFailureReceipt != true) return;
        var current = receipt is null ? null : readContext();
        var partition = SoloAcceptanceFailureReceiptReader.ReadRepairPartition(goal, current, completion);
        if (current is not null && partition?.Findings.Any(finding => finding.Reason is null) == true &&
            readCurrentRevisions() != new GateReadyCandidateRevisionPair(current.CandidateSha, current.MainSha))
            partition = SoloAcceptanceFailureReceiptReader.ReadRepairPartition(goal, null, completion);
        if (partition is not null) RetainFindings(store, goal, partition.Findings);
    }

    private static void RetainFindings(SoloBaselineRepairStore store, Goal goal, IReadOnlyList<SoloRepairFinding> findings)
    {
        var observed = findings.Where(finding => finding.Identity.Length > 0 || finding.LaneEnvironmentVerified).ToArray();
        if (observed.Length > 0)
            store.Retain(goal.Id.Value, observed, proof => CheckRetainedProof(store, goal, proof).Reason is null);
    }

    private static SoloRepairFinding CheckRetainedProof(SoloBaselineRepairStore store, Goal goal, SoloRepairFinding finding)
    {
        if (finding.Reason is not null) return finding;
        try
        {
            if (!finding.LaneEnvironmentVerified)
                throw new InvalidDataException(SoloAcceptanceFailureReceiptReader.LaneEvidenceUnavailable);
            var proof = finding.Proof ?? throw new InvalidDataException(SoloHoldReasons.ProofIdentityMismatch);
            var (request, allowCandidatePass) = SoloBaselineRepairStore
                .AttributionRequests(goal.Id.Value, finding, finding.Candidate, finding.Main)
                .FirstOrDefault(item => proof.Path == Path.Combine(store.ResultDirectory(item.Key), "focused-result.json"));
            if (request is not null)
            {
                var identity = SoloBaselineRepairStore.FocusedProofIdentity(request, finding, finding.Candidate, finding.Main,
                    runBaseline: true, allowCandidatePass: allowCandidatePass);
                var verified = SoloBaselineRepairStore.ReadVerifierResult(proof.Path,
                    store.ResultDirectory(request), identity, proof.Sha256);
                if (verified.Outcome != (finding.Inherited ? "inherited" : "introduced") ||
                    !verified.Artifacts.SequenceEqual(finding.BaselineArtifacts) ||
                    !(verified.CandidateArtifacts ?? []).SequenceEqual(finding.CandidateArtifacts) ||
                    (!finding.Inherited && verified.Feedback != finding.Feedback))
                    throw new InvalidDataException(SoloHoldReasons.ProofIdentityMismatch);
            }
            else
                SoloAcceptanceFailureReceiptReader.VerifyRetainedRepairProof(goal.Id.Value, finding);
            return finding;
        }
        catch (Exception ex) when (SoloAcceptanceFailureReceiptReader.IsRepairEvidenceError(ex))
        {
            return finding with { Inherited = false, Feedback = null, Reason = ex.Message };
        }
    }

    internal static IReadOnlyList<SoloRepairFinding> SoloRepairFindings(
        SoloBaselineRepairStore store, Goal goal, string? candidate, string? main) =>
        store.ReadFindings(goal.Id.Value).Select(finding => CheckRetainedProof(store, goal, finding))
            .Concat(CurrentIntroduced(store, goal, store.ReadFindings(goal.Id.Value, introduced: true), candidate, main)
                .Where(finding => finding.Reason is not null)).ToArray();

    private ConductorAdvanceResult? ReconcileSoloRepairs(Goal goal, string prefix, ConductorAutonomyPolicy policy,
        IReadOnlyList<SoloRepairFinding> findings, GateReadyCandidateRevisionPair? current, bool allowFreshGate)
    {
        if (current?.BranchRevision is not { } candidate || current.MainRevision is not { } main)
            return SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairEvidenceRequired, "Current candidate and integration revisions are required.");

        string? waiting = null;
        string? missing = null;
        string? repairOwnerAction = null;
        var mainIsMergeBase = new Lazy<bool>(() => GitCli.Run(workspace.ExecutionDirectory, "merge-base", candidate, main) is
            { Succeeded: true, DrainTimedOut: false } mergeBase && mergeBase.Output.Trim() == main);
        foreach (var original in findings)
        {
            var finding = original;
            if (!finding.Inherited)
            {
                // A later proof for the same row supersedes an earlier check-only placeholder.
                if (SoloBaselineRepairStore.HasExactReplacement(finding, findings, candidate, main)) continue;
                var movedPair = finding.Candidate != candidate || finding.Main != main;
                if (finding.HasConflictingProofs && !movedPair)
                {
                    missing ??= $"Resolve conflicting verifier proofs for check {finding.Check}, test {finding.Identity}, at {candidate} / {main}.";
                    continue;
                }
                if (finding.Request is null)
                {
                    missing ??= $"Check {finding.Check}, test {finding.Identity}, needs exact verifier evidence " +
                        $"({finding.Reason}, {finding.Proof?.Path ?? "receipt missing"}).";
                    continue;
                }

                // Attribution requires an executed candidate and its merge base at the observed main.
                // Prepare the candidate before charging an attempt for the resulting pair.
                if (!mainIsMergeBase.Value)
                    return prepareAttribution?.Invoke(goal, prefix, policy) ??
                        SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairPending,
                            "Preparing the candidate for focused attribution on current main.");
                var (requestKey, allowCandidatePass) = _store.ResolveAttributionRequest(goal.Id.Value, finding, candidate, main);
                var result = RunSoloRepairCheck(requestKey, finding, candidate, main, runBaseline: true,
                    allowCandidatePass: allowCandidatePass);
                if (result.Outcome == "pending") { waiting ??= finding.Request; continue; }
                if (movedPair && result.Outcome == "passed") continue;
                if (result.Outcome is not ("inherited" or "introduced"))
                {
                    var detail = result.Outcome == "passed"
                        ? "The focused candidate passed and did not confirm the recorded exact failure."
                        : result.Detail;
                    missing ??= $"{finding.Check}, {finding.Identity}, attribution {requestKey}: {detail}";
                    continue;
                }
                if (readCurrentRevisions(goal) != current)
                {
                    waiting ??= "The revision pair moved during attribution.";
                    continue;
                }
                finding = finding with
                {
                    Candidate = candidate, Main = main, Baseline = main,
                    Inherited = result.Outcome == "inherited",
                    BaselineCheck = finding.BaselineCheck ?? (result.Outcome == "inherited" ? result.Detail : null),
                    CandidateArtifacts = result.CandidateArtifacts!, BaselineArtifacts = result.Artifacts,
                    Reason = null, Feedback = result.Outcome == "introduced" ? result.Feedback : null,
                    Proof = result.Proof ?? finding.Proof,
                    LaneEnvironmentVerified = result.LaneEnvironmentVerified
                };
                RetainFindings(_store, goal, [finding]);
                if (_store.HasConflictingProofs(goal.Id.Value, finding))
                {
                    missing ??= $"Resolve conflicting verifier proofs for check {finding.Check}, test {finding.Identity}, at {candidate} / {main}.";
                    continue;
                }
                if (!finding.Inherited) continue;
            }

            var groups = _store.ReadTestGroups(finding);
            var group = groups.FirstOrDefault(group => group.RepairGoal == goal.Id.Value) ?? groups.FirstOrDefault();
            var repair = EnsureSoloRepair(goal, finding, group);
            if (repair.Goal?.Id == goal.Id)
            {
                repairOwnerAction ??= repair.Detail;
                continue;
            }
            var repairs = groups.Select(group => group.RepairGoal).OfType<string>();
            if (repair.Goal is { } resolved)
                repairs = repairs.Prepend(resolved.Id.Value);
            var landed = repairs.Distinct(StringComparer.Ordinal)
                .FirstOrDefault(id => ResolveSoloRepairLanding(workspace, new GoalId(id), main) is not null);
            if (landed is null)
            {
                if (repair.Goal is null)
                {
                    if (repair.Pending) waiting ??= repair.Detail;
                    else missing ??= repair.Detail;
                }
                else if (repair.Goal.IsTerminal)
                    missing ??= $"Confirm reachable landing evidence for repair {repair.Goal.Id.Value}, or designate a replacement.";
                else
                    waiting ??= $"Waiting for repair {repair.Goal.Id.Value} to land for {finding.Identity}.";
                continue;
            }

            var validation = _store.ReadRepairValidation(finding, main) ??
                RunSoloRepairCheck(SoloBaselineRepairStore.ValidationKey(finding, main), finding, main, main, false);
            if (validation.Outcome == "pending") waiting ??= finding.Request;
            else if (validation.Outcome == "failed")
                missing ??= $"The original test {finding.Identity} executed and still fails on refreshed baseline {main} " +
                    $"in {validation.Detail}. Decide whether to reopen repair {landed} or designate a follow-up repair.";
            else if (validation.Outcome != "passed")
                missing ??= $"Refreshed baseline {main}, check {finding.Check}, test {finding.Identity}: {validation.Detail}";
        }
        if (repairOwnerAction is not null)
            return SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairOwnerActionRequired, repairOwnerAction);
        if (missing is not null)
            return SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairEvidenceRequired, missing);
        if (waiting is not null || readCurrentRevisions(goal) != current)
            return SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairPending, waiting ?? "Rechecking a changed revision pair.");
        if (!allowFreshGate || ReadIntroduced(goal, current) is not null) return null;
        if (goal.Status != GoalStatus.AcceptanceFailed) return null;
        if (ReadStandingIntroduced(goal, candidate) is { } standing)
        {
            // Advancing main can validate an inherited repair without replacing the failed candidate.
            var route = AttributedFailureDeveloperRoute.Decide(goal, standing, candidate);
            return route.IsRoute
                ? failureHandler().RouteIntroduced(goal, prefix, policy, standing, route)
                : failureHandler().Hold(goal, prefix, policy, route.DeclineReason!);
        }
        return SoloFreshGateAdmission.TryAdmit(kernel, goal, prefix, policy, new(candidate, main, "", ""), SoloHoldReasons.RepairValidated)
            ?? SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairPending, "Waiting for readiness to run normal acceptance.");
    }

    private (Goal? Goal, bool Pending, string Detail) EnsureSoloRepair(Goal dependent, SoloRepairFinding finding, SoloRepairGroup? group)
    {
        const string requestPrefix = "solo-baseline-repair-";
        if (group is null)
        {
            var owners = new PortfolioStore(workspace.PortfolioStorePath).ListEpicsAsync().GetAwaiter().GetResult()
                .Where(epic => epic.Title.Replace(' ', '-').Equals("baseline-maintenance", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (owners.Length != 1)
                return (null, false, $"Designate one baseline-maintenance epic for {finding.Check}, {finding.Identity} on {finding.Baseline}.");
            var objective = $"Repair baseline test failure {finding.Identity} in {finding.Check} on revision {finding.Baseline}. " +
                $"Reproduce using {finding.Request} and make the original exact case pass on the integration branch.";
            var payload = RepairIntake.PrepareRepair(requestPrefix + finding.GroupKey, objective, owners[0].Id);
            group = _store.ReserveGroup(finding.GroupKey, JsonSerializer.Serialize(payload, OperatorIntentJson.Options));
        }

        var requestKey = requestPrefix + group.Key;
        var intake = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        try
        {
            if (group.RepairGoal is null)
            {
                var payload = JsonSerializer.Deserialize<GoalCreationOperatorIntentPayload>(group.IntakePayload, OperatorIntentJson.Options)
                    ?? throw new InvalidOperationException("Canonical repair intake payload is missing.");
                _ = intake.Reserve(requestKey, payload.Fingerprint);
                RepairIntake.ApplyReservedRepair(kernel, payload);
                var created = intake.Get(requestKey)?.GoalId ?? throw new InvalidOperationException("Keyed repair intake returned no goal ID.");
                // CLI intake retries epic assignment during replay. Confirm it before acknowledging
                // the group because the CLI can return after recording an assignment failure.
                var membership = new PortfolioStore(workspace.PortfolioStorePath).GetGoalMembershipAsync(created).GetAwaiter().GetResult();
                if (membership is null)
                    return (null, false, $"Assign canonical repair {created} to baseline-maintenance epic {payload.RequestedEpicId}.");
                _store.AcknowledgeRepair(group.Key, created);
                group = group with { RepairGoal = created };
            }
            var id = new GoalId(group.RepairGoal);
            if (id == dependent.Id)
            {
                _store.RemoveSelfDependency(group.Key, id.Value);
                return (dependent, false, $"The canonical repair {id.Value} still reproduces {finding.Identity}.");
            }
            var repair = kernel.Goals.FirstOrDefault(goal => goal.Id == id);
            if (repair is null)
            {
                var snapshot = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                    .LoadGoalAsync(id).GetAwaiter().GetResult();
                if (snapshot is null) return (null, false, $"Restore canonical repair {id.Value} for group {group.Key}.");
                repair = Goal.FromSnapshot(snapshot);
            }
            return (repair, false, "");
        }
        catch (GoalCreationIntentDeferredException)
        {
            return (null, true, $"Waiting to reconcile repair intake {requestKey}.");
        }
        catch (InvalidOperationException ex)
        {
            return (null, false, $"Resolve repair intake {requestKey}: {ex.Message}");
        }
    }

    internal static string? ResolveSoloRepairLanding(OrchestratorWorkspace workspace, GoalId repair, string main)
    {
        var root = workspace.ExecutionDirectory;
        bool IsReachable(string revision) =>
            GitCli.Run(root, "merge-base", "--is-ancestor", revision, main) is { Succeeded: true, DrainTimedOut: false };

        var repository = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath);
        var promotion = repository.ReadPromotionFactAsync(repair).GetAwaiter().GetResult();
        if (promotion is not null && IsReachable(promotion.PromotedMainRevision))
            return promotion.PromotedMainRevision;

        var journal = GoalOperationJournal.Read(root, repair);
        foreach (var intent in GoalOperationJournal.ReadLandingIntents(journal, repair, GoalWorktreeLayout.BranchName(repair)))
        {
            if (LandingExecutor.HasAdmittedGoalLandedEvent(workspace, repair, intent.MergeCommitSha) && IsReachable(intent.MergeCommitSha))
                return intent.MergeCommitSha;
        }

        var resolver = GoalIntegrationEvidenceResolver.Build(root, workspace.IntegrationBranch, main);
        return resolver.TryResolve(repair, out var evidence) ? evidence!.IntegrateSha : null;
    }

    private SoloRepairCheckReceipt RunSoloRepairCheck(string key, SoloRepairFinding finding, string checkoutSha,
        string baselineSha, bool runBaseline, bool allowCandidatePass = false)
    {
        var proofIdentity = SoloBaselineRepairStore.FocusedProofIdentity(key, finding, checkoutSha, baselineSha,
            runBaseline, allowCandidatePass);
        if (_store.ReadVerifiedCheck(key, proofIdentity) is { } prior) return prior;
        if (finding.Request is null)
            return new("unresolved", "An exact focused verifier execution is required.", []);
        var request = new SoloRepairCheckRequest(key, finding, checkoutSha, baselineSha, runBaseline,
            allowCandidatePass, proofIdentity);
        if (checkAttempts is null)
            return SoloRepairCheckRunner.Run(workspace, _store, verifier, acquireStableSlot, request);

        checkAttempts.StartOrObserve(_store, request);
        return new("pending", key, []);
    }

    internal int CountLiveCheckAttempts() => checkAttempts?.CountLiveAttempts() ?? 0;

    internal bool HasObligations(Goal goal) => _store.ReadFindings(goal.Id.Value).Count > 0;

    internal bool TryDeclineAdmission(Goal goal, string? candidate, string? main, out string? reason)
    {
        var ready = _store.IsEvidenceCurrent(goal.Id.Value, SoloRepairFindings(_store, goal, candidate, main), candidate, main);
        reason = ready ? null : SoloHoldReasons.RepairPending;
        return !ready;
    }

    internal ConductorAdvanceResult? HoldIfUnprepared(Goal goal, string prefix, ConductorAutonomyPolicy policy)
    {
        if (!HasObligations(goal) && _store.ReadFindings(goal.Id.Value, introduced: true).Count == 0) return null;
        var current = readCurrentRevisions(goal);
        var findings = SoloRepairFindings(_store, goal, current?.BranchRevision, current?.MainRevision);
        return _store.IsEvidenceCurrent(goal.Id.Value, findings, current?.BranchRevision, current?.MainRevision)
            ? null
            : SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairPending, "Revalidating the prepared candidate's baseline.");
    }

    internal ConductorAdvanceResult? ObserveAcceptedPair(Goal goal, string prefix, ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance)
    {
        if (!acceptance.Passed || acceptance.RequiredUnmetCriteria.Count > 0) return null;
        var findings = SoloRepairFindings(_store, goal, acceptance.BranchHeadSha, acceptance.MainHeadSha);
        if (findings.Count == 0) return null;
        if (acceptance.BranchHeadSha is not { } candidate || acceptance.MainHeadSha is not { } main ||
            readCurrentRevisions(goal) is not { } current || current.BranchRevision != candidate ||
            current.MainRevision != main || !_store.IsEvidenceCurrent(goal.Id.Value, findings, candidate, main))
            return SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairPending,
                "The completed acceptance does not cover the current candidate and validated baseline pair.");
        _store.AcknowledgeEffect(SoloBaselineRepairStore.NormalGateKey(goal.Id.Value, candidate, main),
            acceptance.AttemptId ?? "inline-gate");
        return null;
    }

    internal static (bool Acknowledged, string? RecoverableAttempt, string? Blocker) ReadNormalGate(
        string root, SoloBaselineRepairStore store, Goal goal, GateReadyCandidateRevisionPair pair)
    {
        if (pair is not { BranchRevision: { } candidate, MainRevision: { } main }) return default;
        if (!GitRevisionReader.IsBranchObjectId(candidate) || !GitRevisionReader.IsBranchObjectId(main)) return default;

        GoalOperationJournalSummary journal;
        try
        {
            journal = GoalOperationJournal.ReadStrict(root, goal.Id);
        }
        catch (Exception error) when (error is FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return (false, null, $"Cannot read normal acceptance evidence for goal {goal.Id.Value}. {error.Message} " +
                "Restore a readable goal operation journal before retrying acceptance");
        }

        // The strict reader keeps entries in chronological order, with file order breaking ties.
        var latest = journal.Entries.LastOrDefault(entry =>
            (!string.IsNullOrWhiteSpace(entry.AcceptanceOutcome) ||
             (entry.Status != GoalOperationStatus.Begin &&
              (IsCliAcceptance(entry) || IsConductorAcceptance(entry)))) &&
            entry.CouldMatchCandidate(candidate, main));
        if (latest is null) return default;
        if (!latest.HasCandidatePair)
            return (false, null, $"Normal acceptance outcome '{latest.IdempotencyKey}' has an incomplete revision pair " +
                $"in '{journal.Path}'. Restore its recorded candidate and integration revisions before retrying acceptance");
        if (latest.Status != GoalOperationStatus.Completed || latest.GoalId != goal.Id ||
            !latest.HasCandidate(candidate, main)) return default;

        var cliAcceptance = IsCliAcceptance(latest);
        var passed = cliAcceptance
            ? latest.RequiredChecksValidated && GoalOperationJournalGatePassedOutcome.Matches(latest)
            : IsConductorAcceptance(latest) && latest.AcceptanceOutcome == "passed";
        if (!passed) return default;

        var acknowledged = store.ReadCheck(SoloBaselineRepairStore.NormalGateKey(goal.Id.Value, candidate, main))?.Outcome == "passed";
        return (acknowledged, cliAcceptance ? latest.IdempotencyKey : null, null);

        static bool IsCliAcceptance(GoalOperationJournalEntry entry) =>
            string.Equals(entry.Operation, "acceptance", StringComparison.OrdinalIgnoreCase);

        static bool IsConductorAcceptance(GoalOperationJournalEntry entry) =>
            string.Equals(entry.Operation, "conductor:acceptance", StringComparison.OrdinalIgnoreCase);
    }

    private ConductorAdvanceResult SoloRepairHold(Goal goal, string prefix, ConductorAutonomyPolicy policy, string reason, string detail)
    {
        var hold = CreateSoloRepairHold(GoalLifecycle.ResolveState(goal, getFacts(goal)), reason, detail);
        if (hold.Owner == ConductorHoldOwner.Operator)
            PublishSoloRepairEscalation(goal, hold.State, hold.Reason, hold.StableIdentity!);
        return new(goal.Id.Value, prefix, policy.Name, hold);
    }

    internal static ConductorAdvanceOutcome.Held CreateSoloRepairHold(GoalLifecycleState state, string reason, string detail)
    {
        var next = SoloHoldNextAction.For(reason);
        var message = $"{reason}. {detail} {next.NextAction}";
        var identity = SoloBaselineRepairStore.Key(reason, detail);
        return new ConductorAdvanceOutcome.Held(state, message, StableIdentity: identity)
        {
            Owner = reason == SoloHoldReasons.RepairEvidenceRequired ? ConductorHoldOwner.Operator : ConductorHoldOwner.None,
            Decision = new("solo-baseline-repair", "Hold", 1, reason, detail,
                [new("holdIdentity", identity), new("owner", next.Owner), new("nextAction", next.NextAction)])
        };
    }

    private void PublishSoloRepairEscalation(Goal goal, GoalLifecycleState state, string message, string identity)
    {
        var key = SoloBaselineRepairStore.Key("escalation", goal.Id.Value, identity);
        if (_store.ReadCheck(key) is not null) return;
        using var ownership = new Mutex(false, "mcg-solo-repair-" + SoloBaselineRepairStore.Key(workspace.SqliteStatePath, key));
        try
        {
            if (!ownership.WaitOne(0)) return;
        }
        catch (AbandonedMutexException) { }
        try
        {
            if (_store.ReadCheck(key) is null && writeEscalation(goal, state, message, key).Delivered)
                _store.AcknowledgeEffect(key, message);
        }
        finally { ownership.ReleaseMutex(); }
    }
}
