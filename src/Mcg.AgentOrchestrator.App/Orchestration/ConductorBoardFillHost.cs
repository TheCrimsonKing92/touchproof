using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorBoardFillHost
{
    private readonly ConductorBoardFillDraftStore _store;
    private readonly Func<string, CancellationToken, AuthorBriefDraftOutcome> _draft;
    private readonly Func<IReadOnlyList<BacklogItem>> _backlog;
    internal readonly Func<AgentOrchestratorKernel, IReadOnlyList<BacklogItem>, Func<BacklogItem, BacklogReadiness>> _readiness;
    private readonly Func<ConductorAutonomyPolicy> _policy;
    private readonly ConductEventLogWriter _events;
    private readonly BoardBelowTargetMonitor _belowTarget = new();
    private readonly BoardFillEpicMissingQuestion _epicMembership;
    private readonly Func<DateTimeOffset> _utcNow;
    private readonly Func<string?> _mainHead;
    private readonly Func<string?> _staleMainHead;
    private readonly Func<IReadOnlyList<string>>? _preferredOrder;
    private readonly StalledEpicAttention? _stalledEpics;
    private readonly BoardFillTrackedEditsHold _trackedEditsHold;
    private readonly IBoardFillPremiseVerifier? _verifier;
    private readonly Func<BoardFillVerdictRevisionRequest, CancellationToken, Task<BoardFillVerdictRevisionResult>>? _revise;
    private readonly Action<string, string>? _appendBacklogNote;
    private readonly Func<string, string, IReadOnlyList<string>?>? _changedFiles;
    private readonly CancellationTokenSource _shutdown = new();
    private BoardFillDraftRound? _running;
    private Task<BoardFillRoundResult>? _round;
    private AgentOrchestratorKernel? _kernel;
    private bool _recovered;
    // In memory only; a process restart clears the hold.
    private string? _heldMainHead;
    private string? _lastPlanReadFailure;
    private readonly HashSet<string> _reportedStoppedItems = new(StringComparer.Ordinal);
    private DateTime _stoppedReportDay;
    private string? _reportedBackoffRound;
    private string? _lastReadinessDetail;
    private (string Detail, string EventId, DateTimeOffset ObservedAt)? _pendingReadiness;
    internal Task? CurrentRound => _round;

    // Drafting is read-only; optional filing delegates all goal mutations to the CLI intake seam.
    internal ConductorBoardFillHost(ConductorBoardFillDraftStore store,
        Func<string, CancellationToken, AuthorBriefDraftOutcome> draft,
        Func<IReadOnlyList<BacklogItem>> backlog,
        Func<AgentOrchestratorKernel, IReadOnlyList<BacklogItem>, Func<BacklogItem, BacklogReadiness>> readiness,
        Func<ConductorAutonomyPolicy> policy, ConductEventLogWriter events,
        Func<IReadOnlyList<BacklogItem>, IReadOnlySet<string>> epicMembership, Func<DateTimeOffset>? utcNow = null,
        IBoardFillPremiseVerifier? verifier = null, BoardFillFilingSeams? filing = null,
        Func<string?>? mainHead = null, Func<(bool Clean, AuthorDraftTrackedEdits? Edits)>? trackedEdits = null,
        Func<string?>? staleMainHead = null, Func<IReadOnlyList<string>>? preferredOrder = null,
        StalledEpicAttention? stalledEpics = null,
        Func<BoardFillVerdictRevisionRequest, CancellationToken, Task<BoardFillVerdictRevisionResult>>? revise = null,
        Action<string, string>? appendBacklogNote = null,
        Func<string, string, IReadOnlyList<string>?>? changedFiles = null,
        ICollaborationItemStore? epicAttention = null)
    {
        _store = store;
        _draft = draft;
        _backlog = backlog;
        _readiness = readiness;
        _policy = policy;
        _events = events;
        _epicMembership = new(epicMembership, events, epicAttention);
        _utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
        _verifier = verifier;
        _revise = revise;
        _appendBacklogNote = appendBacklogNote;
        _changedFiles = changedFiles;
        _filingSeams = filing;
        _mainHead = mainHead ?? (() => null);
        _staleMainHead = staleMainHead ?? _mainHead;
        _preferredOrder = preferredOrder;
        _stalledEpics = stalledEpics;
        _trackedEditsHold = new(store, events, trackedEdits ?? (() => (false, null)));
    }

    internal void ServiceTick(AgentOrchestratorKernel kernel, string? onlyGoalId = null)
    {
        if (onlyGoalId is not null || _shutdown.IsCancellationRequested) return;
        var policy = _policy();
        if (policy.BoardFillMode == ConductorBoardFillMode.Off)
        {
            if (_round is { IsCompleted: true })
            {
                _kernel = kernel;
                Harvest(_utcNow());
                ReportFinished();
            }
            return;
        }
        var errors = policy.Validate();
        if (errors.Count != 0) throw new InvalidOperationException(string.Join("; ", errors));
        var now = _utcNow();
        var reason = ServiceDraftTick(kernel, policy, now);
        var detail = _belowTarget.Observe(kernel.Goals.Count(goal => !goal.IsTerminal),
            policy.BoardFillTargetActiveGoals, _round is not null || _filing is not null ? null : reason, now);
        if (detail is not null) _events.AppendRequired("board-fill-draft", null, detail, now);
    }

    private string? ServiceDraftTick(AgentOrchestratorKernel kernel, ConductorAutonomyPolicy policy,
        DateTimeOffset now)
    {
        _stalledEpics?.Service(now);
        _kernel = kernel;
        if (!_recovered)
        {
            foreach (var interrupted in _store.ReadAll().Where(round => round.Outcome is null))
                _store.Finish(interrupted, Failed("interrupted"), now);
            foreach (var unfinished in _store.ReadAll().Where(round => round.Outcome is not null && round.Assessment is null))
                Assess(unfinished, null, new("unavailable", 0, [], "assessment-interrupted"), kernel);
            _recovered = true;
        }
        var held = Harvest(now);
        ReportFinished();
        var items = ConductorTickStepLedger.CountBacklogRows(_backlog());
        var epicMembers = _epicMembership.Read(items, now);
        if (epicMembers is not null) ServiceFiling(items, epicMembers);
        var newestFirst = _store.ReadAll(newestFirst: true);
        var rounds = newestFirst.Reverse().ToArray();
        var goals = kernel.Goals.ToArray();
        var admission = epicMembers is null ? (Allowed: false, Reason: (string?)null)
            : DraftAdmission(goals, rounds, policy, held, now);
        string? observedMain;
        try { observedMain = _staleMainHead(); }
        catch { observedMain = null; }
        // Record evidence releases before selection so a new round captures the durable item stamp.
        if (admission.Allowed) items = ReleaseContradictionHolds(items, observedMain);
        var stopped = BoardFillConsecutiveNonDraftRounds.StoppedItems(rounds, now);
        var excluded = ConductorBoardFillDraftStore.AlreadyDrafted(items, observedMain, newestFirst);
        excluded.UnionWith(stopped.Keys);
        Func<BacklogItem, BacklogReadiness>? readiness = null;
        var evaluate = ConductorTickStepLedger.CountReadinessEvaluations(item => (readiness ??= _readiness(kernel, items))(item));
        var candidates = BoardFillReadyItemSelector.EvaluateCandidates(items, goals, excluded, evaluate, rounds, observedMain).ToArray();
        ReportReadiness(candidates, now);
        if (!admission.Allowed) return admission.Reason;

        var preferredOrder = ReadPreferredOrder(now);
        ReportStoppedItems(items, stopped, now);
        if (BoardFillFailureBreaker.Evaluate(rounds, now) is { } backoff)
        {
            var detail = $"BOARD_FILL_BACKOFF consecutive={backoff.ConsecutiveCount} " +
                $"delay={backoff.Delay.TotalSeconds}s retry={backoff.RetryAt:O} round={backoff.RoundId}";
            if (_reportedBackoffRound != backoff.RoundId &&
                _events.AppendRequired("board-fill-draft", null, detail, now, "board-fill-backoff-" + backoff.RoundId))
                _reportedBackoffRound = backoff.RoundId;
            return policy.BoardFillMode == ConductorBoardFillMode.Shadow
                ? BoardBelowTargetMonitor.ModeNotFile : BoardBelowTargetMonitor.AuthorRefusalStreak;
        }
        var item = BoardFillReadyItemSelector.SelectReady(candidates, preferredOrder,
            epicMembers, missing => _epicMembership.Ask(missing, now),
            nearDuplicate: BoardFillNearDuplicate.Lookup(items, goals, rounds, now),
            nearDuplicateSkipped: AnnotateNearDuplicate);
        if (item is null)
            return policy.BoardFillMode == ConductorBoardFillMode.Shadow ? BoardBelowTargetMonitor.ModeNotFile
                : stopped.Count > 0 ? BoardBelowTargetMonitor.AuthorRefusalStreak : BoardBelowTargetMonitor.NoReadyItem;
        _running = _store.Begin(item, now);
        // Only immutable identity crosses the thread boundary; the model never reads the live kernel.
        _round = Task.Run(() => RunRoundAsync(item.Id));
        return null;
    }

    private (bool Allowed, string? Reason) DraftAdmission(IReadOnlyList<Goal> goals,
        IReadOnlyList<BoardFillDraftRound> rounds, ConductorAutonomyPolicy policy, bool held, DateTimeOffset now)
    {
        // Admission refreshes membership on the filing thread; wait for that observation before spending another draft.
        if (held || _round is not null || _filing is not null ||
            goals.Count(goal => !goal.IsTerminal) >= policy.BoardFillTargetActiveGoals) return (false, null);
        if (policy.BoardFillMaxDraftsPerDay is { } cap && BoardFillDailyAllowance.Count(rounds, now) >= cap)
            return (false, policy.BoardFillMode == ConductorBoardFillMode.Shadow
                ? BoardBelowTargetMonitor.ModeNotFile : BoardBelowTargetMonitor.DailyCapReached);
        if (_heldMainHead is not null)
        {
            string? mainHead;
            try { mainHead = _mainHead(); }
            catch { return (false, null); } // An unresolved head cannot release a repository hold.
            if (string.IsNullOrWhiteSpace(mainHead) ||
                string.Equals(mainHead, _heldMainHead, StringComparison.OrdinalIgnoreCase)) return (false, null);
            _heldMainHead = null;
        }
        return (!_trackedEditsHold.Blocks(now), null);
    }

    private void ReportReadiness(IEnumerable<(BacklogItem Item, BacklogReadiness Ready)> candidates, DateTimeOffset now)
    {
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);
        foreach (var (_, readiness) in candidates)
            if (!readiness.Eligible)
                counts[readiness.Suffix] = counts.GetValueOrDefault(readiness.Suffix) + 1;
        var detail = JsonSerializer.Serialize(counts);
        if (_pendingReadiness is null && detail == _lastReadinessDetail) return;

        // The writer can queue a required event before returning failure. Retry with the same ID.
        var pending = _pendingReadiness ??= (detail, Guid.NewGuid().ToString("N"), now);
        if (!_events.AppendRequired("BOARD_FILL_READINESS", null, pending.Detail, pending.ObservedAt, pending.EventId)) return;
        _lastReadinessDetail = pending.Detail;
        _pendingReadiness = null;
    }

    private void AnnotateNearDuplicate(BacklogItem item, BoardFillNearDuplicateReference reference)
    {
        if (_appendBacklogNote is null || item.Notes.Any(note =>
            note.Text.Contains(BoardFillNearDuplicate.Token(reference), StringComparison.Ordinal))) return;
        try { _appendBacklogNote(item.Id, BoardFillNearDuplicate.Note(reference)); }
        catch (Exception) { } // Selection still skips the item; a later tick can retry the annotation.
    }

    private void ReportStoppedItems(IReadOnlyList<BacklogItem> items,
        IReadOnlyDictionary<string, int> stopped, DateTimeOffset now)
    {
        var day = now.UtcDateTime.Date;
        if (_stoppedReportDay != day)
        {
            _reportedStoppedItems.Clear();
            _stoppedReportDay = day;
        }
        foreach (var item in items.Where(item => item.Status == BacklogItemStatus.Open))
        {
            if (!stopped.TryGetValue(item.Id, out var count) || _reportedStoppedItems.Contains(item.Id)) continue;
            var detail = $"BOARD_FILL_ITEM_STOPPED backlog={item.Id[..Math.Min(8, item.Id.Length)]} " +
                $"item={item.Id} consecutive={count} reason=consecutive-non-draft-rounds";
            if (_events.AppendRequired("board-fill-draft", null, detail, now))
                _reportedStoppedItems.Add(item.Id);
        }
    }

    private IReadOnlyList<BacklogItem> ReleaseContradictionHolds(IReadOnlyList<BacklogItem> items, string? head)
    {
        if (_appendBacklogNote is null || string.IsNullOrWhiteSpace(head)) return items;
        var wrote = false;
        foreach (var item in items.Where(item => item.Status == BacklogItemStatus.Open))
        {
            var note = BoardFillContradictionHold.ActiveMarker(item);
            var marker = note is null ? null : BoardFillContradictionHold.Parse(note.Text);
            if (marker?.EvidenceChanged(head, _changedFiles) != true) continue;
            try
            {
                _appendBacklogNote(item.Id, marker.ReleaseNote(head));
                wrote = true;
            }
            catch (Exception) { } // The marker continues to hold when its release cannot be recorded.
        }
        return wrote ? _backlog() : items;
    }

    private IReadOnlyList<string> ReadPreferredOrder(DateTimeOffset now)
    {
        try
        {
            var order = _preferredOrder?.Invoke() ?? [];
            _lastPlanReadFailure = null;
            return order;
        }
        catch (Exception exception)
        {
            var detail = $"BOARD_FILL_PLAN_READ_FAILED reason={exception.GetType().Name}: {exception.Message}"
                .Replace('\r', ' ').Replace('\n', ' ');
            if (detail != _lastPlanReadFailure && _events.AppendRequired("board-fill-draft", null, detail, now))
                _lastPlanReadFailure = detail;
            return [];
        }
    }

    private bool Harvest(DateTimeOffset now)
    {
        if (_round is not { IsCompleted: true } || _running is null) return false;
        BoardFillRoundResult result;
        try { result = _round.GetAwaiter().GetResult(); }
        catch (Exception exception) { result = new(Failed($"{exception.GetType().Name}: {exception.Message}"), null,
            new("failed", 0, [], exception.GetType().Name)); }
        _store.Finish(_running, result.Outcome, now);
        var finished = _store.ReadAll().Single(round => round.Id == _running.Id);
        _trackedEditsHold.Enter(finished);
        Assess(finished, result.Markdown, result.Verification, _kernel!, result.RevisionAttempted, result.FirstPass);
        _running = null;
        _round = null;
        if (result.Outcome.Kind == "held" && !string.IsNullOrWhiteSpace(result.Outcome.HeldMainHead))
            _heldMainHead = result.Outcome.HeldMainHead;
        return result.Outcome.Kind == "held";
    }

    private void ReportFinished()
    {
        foreach (var round in _store.ReadAll().Where(round => round.Assessment is not null && !round.Reported))
        {
            var detail = $"BOARD_FILL_DRAFT backlog={round.BacklogItemId[..Math.Min(8, round.BacklogItemId.Length)]} " +
                $"outcome={round.Outcome} checks={round.Checks.Count(check => check.Passed)}/{round.Checks.Count} " +
                $"draft={round.DraftPath ?? "none"}";
            var assessment = round.Assessment!;
            detail += $" verified={assessment.Verification.VerifiedCount}/{assessment.Verification.BulletCount}" +
                $" fileable={assessment.Fileable.ToString().ToLowerInvariant()} depends=" +
                (assessment.Scope.Depends.Count == 0 ? "none" : string.Join(',', assessment.Scope.Depends.Select(dependency =>
                    dependency.GoalId[..Math.Min(8, dependency.GoalId.Length)])));
            detail += " reasons=" + FormatDraftReasons(assessment.FailingReasons);
            if (_events.AppendRequired("board-fill-draft", null, detail, round.FinishedAt, "board-fill-" + round.Id))
                _store.MarkReported(round.Id);
        }
    }

    private static string FormatDraftReasons(IReadOnlyList<string> reasons)
    {
        if (reasons.Count == 0) return "none";
        var joined = string.Join("; ", reasons.Select(reason => reason.Replace("\r\n", " ")
            .Replace('\r', ' ').Replace('\n', ' ').Replace('\u0085', ' ')
            .Replace('\u2028', ' ').Replace('\u2029', ' ')));
        return joined.Length > 400 ? joined[..400] + "..." : joined;
    }

    internal void Stop()
    {
        _shutdown.Cancel();
        // Only shutdown may drain. ServiceTick never waits on a drafting task.
        try { _round?.Wait(TimeSpan.FromSeconds(30)); }
        catch (AggregateException) { }
        Harvest(_utcNow());
        if (_running is { } running) _store.Finish(running, Failed("conductor-stop"), _utcNow());
        _running = null;
        _round = null;
        StopFiling();
    }

    private static AuthorBriefDraftOutcome Failed(string reason) => new("failed", 1, null, null, null, [], reason);
}
