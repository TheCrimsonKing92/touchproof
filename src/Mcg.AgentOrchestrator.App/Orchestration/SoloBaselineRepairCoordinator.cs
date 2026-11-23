using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed class SoloBaselineRepairCoordinator(
    OrchestratorWorkspace workspace,
    AgentOrchestratorKernel kernel,
    IGoalAcceptanceVerifier verifier,
    Func<Goal, GateReadyCandidateRevisionPair?> readCurrentRevisions,
    Func<Goal, SoloAcceptanceConsumptionContext?> readContext,
    Func<SoloAcceptanceFailureHandler> failureHandler,
    Func<Goal, GoalLifecycleFacts> getFacts,
    Func<Goal, GoalLifecycleState, string, LandingEscalationWriteResult> writeEscalation,
    Func<string, CancellationToken, DotnetBuildEnvironmentLease> acquireStableSlot)
{
    private readonly SoloBaselineRepairStore _store = new(
        Path.Combine(workspace.OrchestratorDirectory, SoloBaselineRepairStore.FileName));

    internal ConductorAdvanceResult? TryAdvance(Goal goal, string prefix, ConductorAutonomyPolicy policy)
    {
        var partition = goal.LatestAcceptanceFailure?.SoloFailureReceipt is not null
            ? RetainSoloRepairPartition(goal, readContext(goal))
            : null;
        var findings = SoloRepairFindings(goal);
        if (findings.Count == 0) return null;
        var current = readCurrentRevisions(goal);
        var introduced = partition?.Introduced;
        if (introduced is null && current is not null)
            introduced = SoloAcceptanceFailureReceiptReader.RestoreIntroduced(
                _store.ReadFindings(goal.Id.Value, introduced: true).Where(finding =>
                    finding.Candidate == current.BranchRevision && finding.Main == current.MainRevision));
        var repair = ReconcileSoloRepairs(goal, prefix, policy, findings, current, allowFreshGate: introduced is null);
        if (introduced is not null && readCurrentRevisions(goal) is { } routedPair &&
            AttributedFailureDeveloperRoute.Decide(goal, introduced, routedPair.BranchRevision, routedPair.MainRevision) is { IsRoute: true } route)
            return failureHandler().RouteIntroduced(goal, prefix, policy, introduced, route);
        return repair;
    }

    private SoloRepairPartition? RetainSoloRepairPartition(Goal goal, SoloAcceptanceConsumptionContext? current)
    {
        var partition = SoloAcceptanceFailureReceiptReader.ReadRepairPartition(goal, current);
        if (current is not null && partition?.Findings.Any(finding => finding.Inherited) == true &&
            readCurrentRevisions(goal) != new GateReadyCandidateRevisionPair(current.CandidateSha, current.MainSha))
            partition = SoloAcceptanceFailureReceiptReader.ReadRepairPartition(goal, null);
        if (partition is not null) _store.Retain(goal.Id.Value, partition.Findings);
        return partition;
    }

    private IReadOnlyList<SoloRepairFinding> SoloRepairFindings(Goal goal) =>
        _store.ReadFindings(goal.Id.Value);

    private ConductorAdvanceResult? ReconcileSoloRepairs(Goal goal, string prefix, ConductorAutonomyPolicy policy,
        IReadOnlyList<SoloRepairFinding> findings, GateReadyCandidateRevisionPair? current, bool allowFreshGate)
    {
        if (current?.BranchRevision is not { } candidate || current.MainRevision is not { } main)
            return SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairEvidenceRequired, "Current candidate and integration revisions are required.");

        string? waiting = null;
        string? missing = null;
        foreach (var original in findings)
        {
            var finding = original;
            if (!finding.Inherited)
            {
                // A later proof for the same row supersedes an earlier check-only placeholder.
                if (SoloBaselineRepairStore.HasExactReplacement(finding, findings)) continue;
                if (finding.Request is null)
                {
                    missing ??= $"Check {finding.Check}, test {finding.Identity}, needs exact verifier evidence ({finding.Reason}, {finding.Proof.Path}).";
                    continue;
                }

                var samePair = finding.CandidateArtifacts.Count > 0 &&
                    finding.Candidate == candidate && finding.Main == main && finding.Baseline == main;
                var requestKey = SoloBaselineRepairStore.AttributionKey(goal.Id.Value, finding, candidate, main);
                var result = RunSoloRepairCheck(requestKey, finding, samePair ? candidate : main, main, samePair);
                if (result.Outcome == "pending") { waiting ??= finding.Request; continue; }
                if (result.Outcome == "passed") continue;
                if (result.Outcome != "failed" || !samePair)
                {
                    missing ??= $"{finding.Check}, {finding.Identity}, attribution {requestKey}: {result.Detail}";
                    continue;
                }
                finding = finding with { Inherited = true, BaselineCheck = result.Detail,
                    BaselineArtifacts = result.Artifacts, Reason = null };
                if (readCurrentRevisions(goal) != current)
                {
                    waiting ??= "The revision pair moved during attribution.";
                    continue;
                }
                _store.Retain(goal.Id.Value, [finding]);
            }

            var repair = EnsureSoloRepair(goal, finding);
            if (repair.Goal is null)
            {
                if (repair.Pending) waiting ??= repair.Detail;
                else missing ??= repair.Detail;
                continue;
            }
            if (ResolveSoloRepairLanding(repair.Goal, main) is null)
            {
                if (repair.Goal.IsTerminal)
                    missing ??= $"Confirm reachable landing evidence for repair {repair.Goal.Id.Value}, or designate a replacement.";
                else
                    waiting ??= $"Waiting for repair {repair.Goal.Id.Value} to land for {finding.Identity}.";
                continue;
            }

            var validation = RunSoloRepairCheck(SoloBaselineRepairStore.ValidationKey(finding, main), finding, main, main, false);
            if (validation.Outcome == "pending") waiting ??= finding.Request;
            else if (validation.Outcome != "passed")
                missing ??= $"Refreshed baseline {main}, check {finding.Check}, test {finding.Identity}: {validation.Detail}";
        }
        if (missing is not null)
            return SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairEvidenceRequired, missing);
        if (waiting is not null || readCurrentRevisions(goal) != current)
            return SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairPending, waiting ?? "Rechecking a changed revision pair.");
        if (!allowFreshGate) return null;
        if (goal.Status != GoalStatus.AcceptanceFailed) return null;
        return SoloFreshGateAdmission.TryAdmit(kernel, goal, prefix, policy, new(candidate, main, "", ""), SoloHoldReasons.RepairValidated)
            ?? SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairPending, "Waiting for readiness to run normal acceptance.");
    }

    private (Goal? Goal, bool Pending, string Detail) EnsureSoloRepair(Goal dependent, SoloRepairFinding finding)
    {
        var requestKey = "solo-baseline-repair-" + finding.GroupKey;
        var group = _store.ReadGroup(finding.GroupKey);
        if (group is null)
        {
            var owners = new PortfolioStore(workspace.PortfolioStorePath).ListEpicsAsync().GetAwaiter().GetResult()
                .Where(epic => epic.Title.Replace(' ', '-').Equals("baseline-maintenance", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (owners.Length != 1)
                return (null, false, $"Designate one baseline-maintenance epic for {finding.Check}, {finding.Identity} on {finding.Baseline}.");
            var objective = $"Repair baseline test failure {finding.Identity} in {finding.Check} on revision {finding.Baseline}. " +
                $"Reproduce using {finding.Request} and make the original exact case pass on the integration branch.";
            IReadOnlyList<string> arguments = ["goal", objective, "--epic", owners[0].Id, "--request-key", requestKey];
            var agents = AgentCatalogStore.Load(workspace.AgentCatalogPath).Agents;
            var request = GoalIntakeRequestMapper.Map(arguments, workspace, agents)!;
            var payload = new GoalCreationOperatorIntentPayload(requestKey, request.Fingerprint, request.Mode, objective,
                owners[0].Id, arguments, null, agents, false);
            group = _store.ReserveGroup(finding.GroupKey, JsonSerializer.Serialize(payload, OperatorIntentJson.Options));
        }

        var intake = new GoalIntakeRequestStore(workspace.SqliteStatePath);
        try
        {
            if (group.RepairGoal is null)
            {
                var payload = JsonSerializer.Deserialize<GoalCreationOperatorIntentPayload>(group.IntakePayload, OperatorIntentJson.Options)
                    ?? throw new InvalidOperationException("Canonical repair intake payload is missing.");
                _ = intake.Reserve(requestKey, payload.Fingerprint);
                _ = new GoalCreationOperatorIntentHandler(workspace, new CliGoalIntentConstruction())
                    .Apply(kernel, payload, out _);
                var created = intake.Get(requestKey)?.GoalId ?? throw new InvalidOperationException("Keyed repair intake returned no goal ID.");
                if (created == dependent.Id.Value) throw new InvalidOperationException("A repair cannot depend on itself.");
                // CLI intake retries epic assignment during replay. Confirm it before acknowledging
                // the group because the CLI can return after recording an assignment failure.
                var membership = new PortfolioStore(workspace.PortfolioStorePath).GetGoalMembershipAsync(created).GetAwaiter().GetResult();
                if (membership is null)
                    return (null, false, $"Assign canonical repair {created} to baseline-maintenance epic {payload.RequestedEpicId}.");
                _store.AcknowledgeRepair(group.Key, created);
                group = group with { RepairGoal = created };
            }
            var id = new GoalId(group.RepairGoal);
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

    private string? ResolveSoloRepairLanding(Goal repair, string main)
    {
        var root = workspace.ExecutionDirectory;
        var journal = GoalOperationJournal.Read(root, repair.Id);
        if (journal.Entries.Any(entry => entry.Status == GoalOperationStatus.Completed &&
                entry.Operation.Equals("conductor:land", StringComparison.OrdinalIgnoreCase)) &&
            GoalOperationJournal.TryGetLatestLandingIntent(journal)?.MergeCommitSha is { } revision &&
            GitCli.Run(root, "merge-base", "--is-ancestor", revision, main) is { Succeeded: true, DrainTimedOut: false })
            return revision;

        var resolver = GoalIntegrationEvidenceResolver.Build(root, workspace.IntegrationBranch, main);
        return resolver.TryResolve(repair.Id, out var evidence) ? evidence!.IntegrateSha : null;
    }

    private SoloRepairCheckReceipt RunSoloRepairCheck(string key, SoloRepairFinding finding, string checkoutSha,
        string baselineSha, bool runBaseline)
    {
        if (_store.ReadCheck(key) is { } prior) return prior;
        if (finding.Request is null)
            return new("unresolved", "An exact focused verifier execution is required.", []);
        using var ownership = new Mutex(false, "mcg-solo-repair-" + SoloBaselineRepairStore.Key(workspace.SqliteStatePath, key));
        try
        {
            if (!ownership.WaitOne(0)) return new("pending", key, []);
        }
        catch (AbandonedMutexException) { }
        try
        {
            if (!_store.TryClaimCheck(key))
                return _store.ReadCheck(key) ?? new("unresolved", $"Recover verifier results for interrupted request {key}.", []);
            SoloRepairCheckReceipt result;
            try
            {
                result = ExecuteSoloRepairCheck(workspace, verifier, key, finding, checkoutSha, baselineSha, runBaseline);
            }
            catch (Exception ex) when (SoloAcceptanceFailureReceiptReader.IsRepairEvidenceError(ex))
            {
                result = new("unresolved", ex.Message, []);
            }
            _store.CompleteCheck(key, result);
            return result;
        }
        finally { ownership.ReleaseMutex(); }
    }

    private SoloRepairCheckReceipt ExecuteSoloRepairCheck(OrchestratorWorkspace workspace, IGoalAcceptanceVerifier verifier,
        string key, SoloRepairFinding finding, string checkoutSha, string baselineSha, bool runBaseline)
    {
        var root = workspace.ExecutionDirectory;
        var checkout = Path.Combine(OrchestratorTempRoot.GetPurposeDirectory("solo-repair-worktrees"), key);
        var artifacts = Path.Combine(workspace.OrchestratorDirectory, "solo-repair-results", key);
        Directory.CreateDirectory(artifacts);
        using var qos = HostedVerifierRunQos.Begin(workspace);
        var added = GitCli.Run(root, "worktree", "add", "--detach", "--quiet", checkout, checkoutSha);
        try
        {
            if (!added.Succeeded || added.DrainTimedOut || GoalWorktrees.ResolveRequiredRef(checkout, "HEAD") != checkoutSha)
                return new("unresolved", $"Could not prepare exact revision {checkoutSha} for request {key}.", []);
            var cancellationToken = CancellationToken.None;
            using var lease = acquireStableSlot(key, cancellationToken);
            var result = AcceptanceExecutionRunner.RunFocusedVerification(verifier, checkout, goalId: null, finding.Request!,
                lease.Environment.BuildPermitIndex, lease, runBaseline, cancellationToken, workspace.IntegrationBranch,
                projectHomeDirectory: workspace.ProjectHomeDirectoryOrNull, retainFocusedBaselineArtifacts: runBaseline);
            return SoloAcceptanceFailureReceiptReader.ReadRepairCheck(result,
                runBaseline ? FindingEvidenceArm.Baseline : FindingEvidenceArm.Candidate,
                baselineSha, finding, artifacts);
        }
        finally
        {
            if (Directory.Exists(checkout))
            {
                var remove = GitCli.Run(root, "worktree", "remove", "--force", checkout);
                if (!remove.Succeeded || remove.DrainTimedOut)
                    throw new InvalidOperationException($"Could not remove solo repair checkout {checkout}.");
            }
        }
    }

    internal bool HasObligations(Goal goal) => SoloRepairFindings(goal).Count > 0;

    internal bool TryDeclineAdmission(Goal goal, string? candidate, string? main, out string? reason)
    {
        var ready = _store.IsEvidenceCurrent(goal.Id.Value, SoloRepairFindings(goal), candidate, main);
        reason = ready ? null : SoloHoldReasons.RepairPending;
        return !ready;
    }

    internal ConductorAdvanceResult? HoldIfUnprepared(Goal goal, string prefix, ConductorAutonomyPolicy policy)
    {
        var findings = SoloRepairFindings(goal);
        if (findings.Count == 0) return null;
        var current = readCurrentRevisions(goal);
        return _store.IsEvidenceCurrent(goal.Id.Value, findings, current?.BranchRevision, current?.MainRevision)
            ? null
            : SoloRepairHold(goal, prefix, policy, SoloHoldReasons.RepairPending, "Revalidating the prepared candidate's baseline.");
    }

    internal ConductorAdvanceResult? ObserveAcceptedPair(Goal goal, string prefix, ConductorAutonomyPolicy policy,
        AcceptanceVerificationSummary acceptance)
    {
        if (!acceptance.Passed || acceptance.RequiredUnmetCriteria.Count > 0) return null;
        var findings = SoloRepairFindings(goal);
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

    private ConductorAdvanceResult SoloRepairHold(Goal goal, string prefix, ConductorAutonomyPolicy policy, string reason, string detail)
    {
        var next = SoloHoldNextAction.For(reason);
        var state = GoalLifecycle.ResolveState(goal, getFacts(goal));
        var message = $"{reason}. {detail} {next.NextAction}";
        var identity = SoloBaselineRepairStore.Key(reason, detail);
        var requiresOperator = reason == SoloHoldReasons.RepairEvidenceRequired;
        if (requiresOperator)
            PublishSoloRepairEscalation(goal, state, message, identity);
        return new ConductorAdvanceResult(goal.Id.Value, prefix, policy.Name, new ConductorAdvanceOutcome.Held(state, message,
            StableIdentity: identity)
        {
            Owner = requiresOperator ? ConductorHoldOwner.Operator : ConductorHoldOwner.None,
            Decision = new("solo-baseline-repair", "Hold", 1, reason, detail,
                [new("holdIdentity", identity), new("owner", next.Owner), new("nextAction", next.NextAction)])
        });
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
            if (_store.ReadCheck(key) is null && writeEscalation(goal, state, message).Delivered)
                _store.AcknowledgeEffect(key, message);
        }
        finally { ownership.ReleaseMutex(); }
    }
}
