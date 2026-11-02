using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// One instance belongs to one conductor run. The lock covers reads and terminal effects together.
internal sealed class PullRequestDeliveryTracker
{
    private readonly AgentOrchestratorKernel _kernel;
    private readonly OrchestratorWorkspace _workspace;
    private readonly IPullRequestStatusReader _reader;
    private readonly IForgeProcessRunner _runner;
    private readonly ICollaborationItemStore _attention;
    private readonly IGoalLifecycleEventWriter? _events;
    private readonly IOperatorChannel? _channel;
    private readonly IClock _clock;
    private readonly Func<TimeSpan> _elapsed;
    private readonly Action<GoalId>? _refreshJournal;
    private readonly Func<string?>? _mutationBlocker;
    private readonly Action<ConductorLandingReceipt>? _landingSink;
    private readonly ConductorParallelAcceptanceAttemptCoordinator? _acceptanceAttempts;
    private readonly object _sync = new();
    private readonly Dictionary<GoalId, TimeSpan> _attempts = [];
    private readonly Dictionary<GoalId, string> _unavailable = [];
    private bool _servicing;

    internal PullRequestDeliveryTracker(AgentOrchestratorKernel kernel, OrchestratorWorkspace workspace,
        IPullRequestStatusReader reader, IForgeProcessRunner runner, ICollaborationItemStore attention,
        IGoalLifecycleEventWriter? eventWriter = null, IOperatorChannel? channel = null, IClock? clock = null,
        Func<TimeSpan>? elapsed = null, Action<GoalId>? refreshJournal = null, Func<string?>? mutationBlocker = null,
        Action<ConductorLandingReceipt>? landingSink = null,
        ConductorParallelAcceptanceAttemptCoordinator? acceptanceAttempts = null)
    {
        _kernel = kernel;
        _workspace = workspace;
        _reader = reader;
        _runner = runner;
        _attention = attention;
        _events = eventWriter;
        _channel = channel;
        _clock = clock ?? new SystemClock();
        var start = Stopwatch.GetTimestamp();
        _elapsed = elapsed ?? (() => Stopwatch.GetElapsedTime(start));
        _refreshJournal = refreshJournal;
        _mutationBlocker = mutationBlocker;
        _landingSink = landingSink;
        _acceptanceAttempts = acceptanceAttempts;
    }

    internal IReadOnlySet<GoalId> Service(IEnumerable<Goal> goals)
    {
        lock (_sync)
        {
            var changed = new HashSet<GoalId>();
            if (_servicing || !PullRequestLandingDelivery.Applies(_workspace)) return changed;
            _servicing = true;
            try
            {
                foreach (var goal in goals.Where(IsEligible))
                {
                    var now = _elapsed();
                    if (_attempts.TryGetValue(goal.Id, out var last) && now - last < TimeSpan.FromMinutes(2)) continue;
                    _attempts[goal.Id] = now;
                    var before = goal.Timeline.Count;
                    var statusBefore = goal.Status;
                    try { Track(goal, goal.DeliveredForReview!); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
                    { Unavailable(goal, exception.Message); }
                    finally
                    {
                        if (goal.Timeline.Count != before || goal.Status != statusBefore) changed.Add(goal.Id);
                    }
                }
            }
            finally { _servicing = false; }
            return changed;
        }
    }

    private void Track(Goal goal, GoalDeliveredForReview delivery)
    {
        var status = _reader.Read(_workspace.ExecutionDirectory, delivery.PullRequestNumber);
        if (!IsEligible(goal) || goal.DeliveredForReview != delivery) return;
        if (status.Kind == PullRequestStatusKind.Unavailable)
        { Unavailable(goal, status.Reason ?? "pull request status unavailable"); return; }
        if (_mutationBlocker?.Invoke() is { Length: > 0 }) return;
        switch (status.Kind)
        {
            case PullRequestStatusKind.Open:
                _unavailable.Remove(goal.Id);
                if (!PullRequestLandingDelivery.HasRefreshStarted(goal)) Refresh(goal, delivery);
                return;
            case PullRequestStatusKind.ClosedUnmerged:
                _unavailable.Remove(goal.Id);
                if (PullRequestLandingDelivery.HasRefreshStarted(goal) && !PullRequestLandingDelivery.RefreshStopped(goal))
                    _kernel.RecordGoalPolicyDecision(goal.Id, PullRequestLandingDelivery.RefreshStoppedPrefix + "closed unmerged " + delivery.Url);
                var reason = PullRequestLandingDelivery.ClosedUnmergedPrefix + delivery.Url;
                if (goal.Timeline.Any(item => item.Kind == ProgressKind.GoalPolicyDecision &&
                    item.OccurredAt >= delivery.DeliveredAt && item.Message == reason)) return;
                OperatorInbox.RecordLandingEscalation(_workspace, goal, reason, _workspace.IntegrationBranch, _channel);
                _events?.AppendGoalEscalated(goal.Id, GoalLifecycleState.Verified, goal.Status, reason, _workspace.IntegrationBranch);
                _kernel.RecordGoalPolicyDecision(goal.Id, reason);
                return;
            case PullRequestStatusKind.Merged:
                if (PullRequestLandingDelivery.HasRefreshStarted(goal) && !PullRequestLandingDelivery.RefreshStopped(goal))
                    _kernel.RecordGoalPolicyDecision(goal.Id, PullRequestLandingDelivery.RefreshStoppedPrefix + "merged " + delivery.Url);
                if (!IsRevision(status.MergeCommit)) { Unavailable(goal, "merged response has no usable merge commit"); return; }
                Land(goal, delivery, status.MergeCommit!);
                return;
            default: throw new InvalidOperationException("Unknown pull request tracking outcome.");
        }
    }

    private void Land(Goal goal, GoalDeliveredForReview delivery, string merge)
    {
        var root = _workspace.ExecutionDirectory;
        var branch = _workspace.IntegrationBranch;
        var remote = "refs/remotes/origin/" + branch;
        if (!Fetch(goal)) return;
        var onTrunk = Git("merge-base", "--is-ancestor", merge, remote);
        if (!Ok(onTrunk)) { Unavailable(goal, "merge commit not confirmed on fetched trunk"); return; }
        var parent = Git("rev-parse", "--verify", merge + "^1");
        if (!Ok(parent) || !IsRevision(parent.Output.Trim()))
        { Unavailable(goal, "could not resolve merge parent"); return; }
        var changes = Git("diff", "--name-only", "-z", parent.Output.Trim(), merge);
        if (!Ok(changes)) { Unavailable(goal, "could not read merged file scope"); return; }
        var forward = PullRequestTrunkFastForward.Advance(root, branch, merge);
        if (forward.Unavailable is { } unavailable) { Unavailable(goal, unavailable); return; }
        Reconcile(goal, delivery, merge, forward);
        LandingExecutor.RecordSuccessfulPromotion(_workspace, _kernel, [(goal.Id, delivery.PushedCommit)],
            parent.Output.Trim(), merge, GoalPromotionFact.PullRequestMergedProvenance, _clock);
        var message = $"Pull request merged: {delivery.Url} at {merge}";
        GoalOperationJournal.Completed(root, goal, "conductor:land", message);
        _refreshJournal?.Invoke(goal.Id);
        _events?.AppendGoalLanded(goal.Id, branch, GoalWorktreeLayout.BranchName(goal.Id));
        _landingSink?.Invoke(new(goal.Id.Value, changes.Output.Split('\0', StringSplitOptions.RemoveEmptyEntries), merge));
        OperatorInbox.ClearOwnershipHoldsAfterLanding(_workspace, goal, $"land {goal.Id.Value[..8]}");
        if (goal.SourceBacklogItemId is not null)
            GoalLandingPostActions.AutoCloseSourceBacklogItem(goal, _workspace.BacklogStorePath,
                Console.WriteLine, _kernel, root, integrateCommitSha: merge, stateDbPath: _workspace.SqliteStatePath);
        _kernel.CompleteGoal(goal.Id, message);
        _unavailable.Remove(goal.Id);
    }

    private bool Fetch(Goal goal)
    {
        var root = _workspace.ExecutionDirectory;
        var branch = _workspace.IntegrationBranch;
        var environment = new Dictionary<string, string>
        {
            ["GIT_CEILING_DIRECTORIES"] = Directory.GetParent(Path.GetFullPath(root))?.FullName ?? root,
            ["GIT_TERMINAL_PROMPT"] = "0"
        };
        var fetch = _runner.Run("git", ["fetch", "--no-tags", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}"],
            root, environment, TimeSpan.FromSeconds(60));
        if (fetch.Started && !fetch.TimedOut && fetch.ExitCode == 0) return true;
        Unavailable(goal, $"trunk fetch failed: exit={fetch.ExitCode} timedOut={fetch.TimedOut}: {fetch.StandardError}");
        return false;
    }

    private void Refresh(Goal goal, GoalDeliveredForReview delivery)
    {
        // Recovery owns failed attempts and operator holds; drift never starts a retry loop.
        if (PullRequestLandingDelivery.RefreshStopped(goal) || goal.Status != GoalStatus.Verified ||
            goal.CurrentHold?.Blocker != "delivered for review: " + delivery.Url) return;
        if (!Fetch(goal)) return;
        var remote = Git("rev-parse", "--verify", "refs/remotes/origin/" + _workspace.IntegrationBranch);
        if (!Ok(remote) || !IsRevision(remote.Output.Trim()))
        { Unavailable(goal, "could not resolve fetched trunk tip"); return; }
        var trunk = remote.Output.Trim();
        var contains = Git("merge-base", "--is-ancestor", trunk, delivery.PushedCommit);
        if (Ok(contains)) return;
        if (!NegativeAncestry(contains)) { Unavailable(goal, "could not establish pushed commit ancestry"); return; }
        var forward = PullRequestTrunkFastForward.Advance(_workspace.ExecutionDirectory, _workspace.IntegrationBranch, trunk);
        if (forward.Unavailable is { } unavailable) { Unavailable(goal, unavailable); return; }
        if (forward.Reconcile is not null) { Reconcile(goal, delivery, trunk, forward); return; }
        var refresh = $"{PullRequestLandingDelivery.RefreshStartedPrefix}{delivery.Url} trunk={trunk} previous={delivery.PushedCommit}";
        var attempt = _acceptanceAttempts?.TryReadLatest(goal.Id.Value);
        if (attempt is null || !_acceptanceAttempts!.TryInvalidatePassedReceipt(goal, attempt.AttemptId,
            delivery.PushedCommit, ConductorEvidenceSupersessionCause.CandidateChanged, refresh, () =>
            {
                var candidate = Git("rev-parse", "--verify", GoalWorktreeLayout.BranchName(goal.Id));
                return Ok(candidate) ? candidate.Output.Trim() : null;
            }))
        { Unavailable(goal, "could not invalidate delivered gate receipt for pull request refresh"); return; }
        _kernel.RecordGoalPolicyDecision(goal.Id, refresh);
        _kernel.ClearGoalHold(goal.Id);
    }

    private void Reconcile(Goal goal, GoalDeliveredForReview delivery, string target, PullRequestTrunkFastForward.Result forward)
    {
        var key = $"pull-request-reconcile:{goal.Id.Value}:{target}";
        if (forward.Reconcile is null || _attention.ListAsync(goal.Id.Value).GetAwaiter().GetResult().Any(item => item.CorrelationKey == key)) return;
        _attention.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value, "Reconcile local checkout after pull request merge",
            ForgeDiagnosticRedactor.Redact($"Reconcile local checkout in {_workspace.ExecutionDirectory} ({_workspace.ProjectName}), branch {_workspace.IntegrationBranch}. " +
                $"Observed local commit {forward.LocalTip}; pull request merge commit {target}; {delivery.Url}. {forward.Reconcile}. " +
                "Reconcile the checkout without resetting or force-updating the branch."), key).GetAwaiter().GetResult();
    }

    private GitCli.GitResult Git(params string[] args) => LandingExecutor.GitRunner(_workspace.ExecutionDirectory, args);
    private static bool Ok(GitCli.GitResult result) => result.Succeeded && result.ProcessStarted && !result.DrainTimedOut;
    private static bool NegativeAncestry(GitCli.GitResult result) => result.ExitCode == 1 && result.ProcessStarted && !result.DrainTimedOut;
    private static bool IsRevision(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    internal static bool IsEligible(Goal goal) => goal.DeliveredForReview is { } delivery &&
        ((goal.Status == GoalStatus.Verified && goal.CurrentHold?.Blocker == "delivered for review: " + delivery.Url) ||
         (goal.Status is GoalStatus.Verified or GoalStatus.Verifying &&
          PullRequestLandingDelivery.HasRefreshStarted(goal)));
    private void Unavailable(Goal goal, string reason)
    {
        reason = ForgeDiagnosticRedactor.Redact(reason);
        if (_unavailable.GetValueOrDefault(goal.Id) == reason) return;
        _unavailable[goal.Id] = reason;
        Console.WriteLine($"Pull request tracking {goal.Id.Value[..8]} unavailable: {reason}");
    }
}
