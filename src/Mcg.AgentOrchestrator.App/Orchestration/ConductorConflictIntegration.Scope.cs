using System.Text.Json;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed partial class ConductorConflictIntegration
{
    private const string ScopeRequirementPrefix = "integration-scope:";

    private sealed record ScopeDecision(string IntentId, string IdempotencyKey, string PayloadJson, IntegrationScopeAuthorization Request,
        string Actor, string Channel, string AuthenticationAssurance, DateTimeOffset RequestedAt, DateTimeOffset AppliedAt);
    private sealed record AdmissionScope(string GoalId, string? RoundId, string OriginalHead, string MainSha,
        DateTimeOffset? RetryAt, string[] ApprovalIds, string[] Paths, int? DispatchOrdinal = null);

    private static bool IsCurrentAdmission(Round round, TaskSpec task)
    {
        if (!round.Admitted || round.Admission is not { } admission ||
            admission.RetryAt != task.LatestRetryAt) return false;
        // Older snapshots may settle an already running/completed attempt, but cannot
        // authorize redispatch after it returns to Assigned without an attempt binding.
        if (admission.DispatchOrdinal is not { } ordinal)
            return task.Status is WorkTaskStatus.Running or WorkTaskStatus.Completed ||
                (task.Status == WorkTaskStatus.WaitingForHuman && task.LastProcess is { IsRunning: true });
        // Admission precedes dispatch preparation. The task's durable, append-only history
        // identifies that next attempt even when an answered question leaves RetryAt unchanged.
        // An ended attempt returned to Assigned needs a fresh snapshot; an active attempt
        // (or its result awaiting settlement) retains its captured authority.
        return ordinal > 0 && (task.DispatchHistory.Count == ordinal - 1 ||
            (task.DispatchHistory.Count == ordinal && task.Status != WorkTaskStatus.Assigned));
    }

    internal void ApplyScope(Goal goal, OperatorIntentRecord intent)
    {
        var request = JsonSerializer.Deserialize<IntegrationScopeAuthorization>(intent.PayloadJson, OperatorIntentJson.Options)
            ?? throw new InvalidOperationException("Missing integration scope request.");
        if (intent.ActorKind != OperatorActorKind.Human || intent.AuthenticationAssurance != "local-process" ||
            string.IsNullOrWhiteSpace(intent.Actor))
            throw new InvalidOperationException("Integration scope requires a human local operator.");
        if (request.GoalId != goal.Id.Value || intent.GoalId != goal.Id.Value ||
            !Guid.TryParseExact(request.RoundId, "N", out _) || !FullSha(request.OriginalHead) || !FullSha(request.MainSha) ||
            string.IsNullOrWhiteSpace(request.Reason) || string.IsNullOrWhiteSpace(intent.IdempotencyKey))
            throw new InvalidOperationException("Integration scope requires exact goal, round, original/main SHAs, reason and idempotency key.");
        ReconcileScopeRequirements(goal);
        // The journal effect, rather than a timeline message, proves replay. Never rewrite the old grant,
        // including after revocation or terminal settlement.
        var prior = GoalOperationJournal.Read(executionDirectory, goal.Id).Entries
            .Where(item => item.Operation == OperationName).SelectMany(item => ReadRound(item).ScopeDecisions ?? [])
            .FirstOrDefault(item => item.IntentId == intent.Id);
        if (prior is not null)
        {
            if (prior.PayloadJson != intent.PayloadJson ||
                prior.Actor != intent.Actor || prior.Channel != intent.Channel || prior.IdempotencyKey != intent.IdempotencyKey ||
                prior.AuthenticationAssurance != intent.AuthenticationAssurance)
                throw new InvalidOperationException("Integration scope replay does not match its durable provenance.");
            ResolveScopeRequirement(goal, request.RoundId);
            return;
        }
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id)
            ?? throw new InvalidOperationException("Integration worktree missing.");
        var round = RetireStaleRound(goal, worktree, "scope")
            ?? throw new InvalidOperationException("Integration scope requires an open integration round.");
        if (round.RoundId != request.RoundId || round.OriginalHead != request.OriginalHead || round.MainSha != request.MainSha ||
            ReadRef(worktree, integrationBranch) != request.MainSha)
            throw new InvalidOperationException("Integration scope identity mismatch; inspect the current open round.");
        var paths = NormalizeScopePaths(worktree, request.Paths);
        if (!Enum.IsDefined(request.Action) ||
            (request.Action == IntegrationScopeAction.Approve ? paths.Length == 0 || request.ApprovalId is not null : paths.Length != 0) ||
            (request.Action == IntegrationScopeAction.OriginalOnly && request.ApprovalId is not null))
            throw new InvalidOperationException("Approve requires exact paths; original-only/revoke do not accept paths.");
        if (request.Action == IntegrationScopeAction.Revoke &&
            !ActiveApprovals(round).Any(item => item.IntentId == request.ApprovalId))
            throw new InvalidOperationException("Revoke requires an active approval id from this round.");
        var decision = new ScopeDecision(intent.Id, intent.IdempotencyKey, intent.PayloadJson, request with { Paths = paths }, intent.Actor,
            intent.Channel, intent.AuthenticationAssurance, intent.CreatedAt, DateTimeOffset.UtcNow);
        var task = goal.Tasks.Single(item => item.Id.Value == round.DeveloperTaskId);
        Save(goal, round with
        {
            ScopeDecisions = [.. round.ScopeDecisions ?? [], decision],
            ScopeRequired = request.Action == IntegrationScopeAction.Revoke,
            RetryAt = task.LatestRetryAt
        }, GoalOperationStatus.Begin);
        if (request.Action != IntegrationScopeAction.Revoke) ResolveScopeRequirement(goal, request.RoundId);
    }

    internal void RequireScopeAfterAnswer(Goal goal, string? taskId)
    {
        var round = OpenRound(goal);
        if (round is null || taskId != round.DeveloperTaskId) return;
        var task = goal.Tasks.Single(item => item.Id.Value == taskId);
        Save(goal, round with { ScopeRequired = !HasScopeDecision(round), RetryAt = task.LatestRetryAt }, GoalOperationStatus.Begin);
    }

    private static bool HasScopeDecision(Round round) => ActiveApprovals(round).Any() ||
        round.ScopeDecisions?.LastOrDefault()?.Request.Action == IntegrationScopeAction.OriginalOnly;

    private bool NeedsScopeDecision(Goal goal, Round round, TaskSpec task) => round.ScopeRequired ||
        (!HasScopeDecision(round) && (task.PendingRetryCause == RetryCause.ContractClarification ||
            kernel.HumanInputRequests.Any(request => request.GoalId == goal.Id && request.TaskId == task.Id &&
                request.IsCompleted && round.RetryAt is { } retryAt && request.RequestedAt >= retryAt)));

    private static IEnumerable<ScopeDecision> ActiveApprovals(Round round) => (round.ScopeDecisions ?? [])
        .Where(item => item.Request.Action == IntegrationScopeAction.Approve && item.Request.RoundId == round.RoundId &&
            item.Request.OriginalHead == round.OriginalHead && item.Request.MainSha == round.MainSha &&
            !(round.ScopeDecisions ?? []).Any(revoke => revoke.Request.Action == IntegrationScopeAction.Revoke &&
                revoke.Request.ApprovalId == item.IntentId));

    private static string[] AdmittedPaths(string goalId, Round round)
    {
        if (round.Admission is not { } admission) return []; // Legacy rounds grant only original conflicts.
        if (admission.GoalId != goalId || admission.RoundId != round.RoundId || admission.OriginalHead != round.OriginalHead ||
            admission.MainSha != round.MainSha || admission.RetryAt != round.RetryAt)
            throw new InvalidOperationException("Integration admission scope identity mismatch.");
        var grants = (round.ScopeDecisions ?? []).Where(item => admission.ApprovalIds.Contains(item.IntentId, StringComparer.Ordinal)).ToArray();
        if (grants.Length != admission.ApprovalIds.Length || grants.Any(item => item.Request.Action != IntegrationScopeAction.Approve ||
            item.Request.GoalId != goalId || item.Request.RoundId != round.RoundId || item.Request.OriginalHead != round.OriginalHead ||
            item.Request.MainSha != round.MainSha) ||
            !SamePaths(admission.Paths, grants.SelectMany(item => item.Request.Paths).Distinct(StringComparer.Ordinal)))
            throw new InvalidOperationException("Integration admission scope lacks matching approval provenance.");
        return admission.Paths;
    }

    private DeveloperBranchIntegrationResult RequireScope(Goal goal, Round round)
    {
        var save = !round.ScopeRequired || round.RoundId is null;
        round = round with { RoundId = round.RoundId ?? Guid.NewGuid().ToString("N"), ScopeRequired = true };
        if (save) Save(goal, round, GoalOperationStatus.Begin);
        var question = ScopeQuestion(goal, round);
        kernel.RequestHumanInputDeduplicated(goal.Id, null, question, kind: HumanWaitKind.Other,
            isAutoDefaultable: false, isDismissible: false,
            questionFingerprint: ScopeRequirementPrefix + round.RoundId,
            blockerFingerprint: ScopeRequirementPrefix + round.RoundId + ":" +
                (round.ScopeDecisions?.LastOrDefault()?.IntentId ?? "initial"), recordDuplicateSuppression: false);
        return new(DeveloperBranchIntegrationStatus.Failed, question, round.ConflictPaths);
    }

    private static string ScopeQuestion(Goal goal, Round round) =>
        $"Integration scope confirmation required. Use integration-scope {goal.Id.Value} {round.RoundId} {round.OriginalHead} {round.MainSha} " +
        "<approve|original-only> --text-file <reason> --idempotency-key <key> [--path <exact-file> ...]. Answer prose grants no path authority.";

    internal void ReconcileScopeRequirements(Goal goal)
    {
        var pending = kernel.HumanInputRequests.Where(item => item.GoalId == goal.Id && !item.IsCompleted &&
            IsScopeRequirement(item)).ToArray();
        if (pending.Length == 0) return;
        var worktree = GoalWorktrees.TryResolve(executionDirectory, goal.Id);
        var round = worktree is not null && Directory.Exists(worktree)
            ? RetireStaleRound(goal, worktree, "scope-reconcile") : OpenRound(goal);
        var entries = GoalOperationJournal.Read(executionDirectory, goal.Id).Entries
            .Where(item => item.Operation == OperationName).ToArray();
        var latestId = entries.LastOrDefault() is { } latest ? ReadRound(latest).RoundId : null;
        foreach (var request in pending)
        {
            var roundId = request.QuestionFingerprint[ScopeRequirementPrefix.Length..];
            if (round?.RoundId == roundId)
            {
                // Recover a journaled decision whose kernel checkpoint was interrupted.
                ResolveScopeRequirement(goal, roundId);
                continue;
            }
            var history = entries.Where(item => ReadRound(item).RoundId == roundId).ToArray();
            // Absence of authority is not evidence of retirement. Require a durable terminal
            // outcome or a recorded replacement, and never copy a decision into that replacement.
            if (history.Any(item => item.Status is GoalOperationStatus.Completed or GoalOperationStatus.Failed) ||
                (history.Length > 0 && latestId is not null && latestId != roundId))
                kernel.SubmitHumanInput(request.Id,
                    $"Integration scope requirement retired with round {roundId}; no path authority transferred.",
                    origin: HumanInputAnswerOrigin.Worker, supersedeSiblings: false);
        }
    }

    private void ResolveScopeRequirement(Goal goal, string roundId)
    {
        var round = OpenRound(goal);
        if (round?.RoundId != roundId || round.ScopeRequired || !HasScopeDecision(round)) return;
        foreach (var request in kernel.HumanInputRequests.Where(item => item.GoalId == goal.Id && !item.IsCompleted &&
            item.TaskId is null && item.QuestionFingerprint == ScopeRequirementPrefix + roundId).ToArray())
            kernel.SubmitHumanInput(request.Id, "Resolved by durable typed integration-scope decision.",
                supersedeSiblings: false);
    }

    internal static bool IsScopeRequirement(HumanInputRequest request) => request.TaskId is null &&
        request.QuestionFingerprint.StartsWith(ScopeRequirementPrefix, StringComparison.Ordinal) &&
        Guid.TryParseExact(request.QuestionFingerprint[ScopeRequirementPrefix.Length..], "N", out _);
    internal static bool FullSha(string value) => value is { Length: 40 } && value.All(Uri.IsHexDigit);

    internal static string[] NormalizeScopePaths(string worktree, IReadOnlyList<string>? paths)
    {
        if (paths is null) throw new InvalidOperationException("Integration paths are missing.");
        var normalized = new HashSet<string>(StringComparer.Ordinal);
        var root = Path.GetFullPath(worktree) + Path.DirectorySeparatorChar;
        foreach (var path in paths)
        {
            var file = path.Replace('\\', '/');
            var segments = file.Split('/');
            if (string.IsNullOrWhiteSpace(file) || Path.IsPathRooted(file) || file.IndexOfAny(['*', '?', ':', '\0', '\r', '\n']) >= 0 ||
                segments.Any(item => item is "" or "." or ".." || item.TrimEnd(' ', '.') != item || item.Equals(".git", StringComparison.OrdinalIgnoreCase)) ||
                segments[0].StartsWith(".orchestrator", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Integration scope accepts exact repository files, without metadata, traversal, directories or globs.");
            var absolute = Path.GetFullPath(file, worktree);
            if (!absolute.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) || Directory.Exists(absolute))
                throw new InvalidOperationException("Integration scope path is outside the repository or names a directory.");
            for (var probe = absolute; probe.Length >= root.Length; probe = Path.GetDirectoryName(probe)!)
                if ((File.Exists(probe) || Directory.Exists(probe)) && (File.GetAttributes(probe) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("Integration scope cannot traverse a linked repository path.");
            normalized.Add(file);
        }
        return normalized.Order(StringComparer.Ordinal).ToArray();
    }
}
