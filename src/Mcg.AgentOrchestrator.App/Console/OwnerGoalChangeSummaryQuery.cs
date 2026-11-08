using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerGoalRangeReadCache;
using static Mcg.AgentOrchestrator.App.OwnerConsole.OwnerGoalSummarySectionReader;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

// Preserve the applicability basis even when no acceptance event matches it.
internal sealed record OwnerGoalAcceptanceEvidence(string MainRevision, GoalOperationJournalEntry? Outcome);

// Selected-goal composition only. No worker polling, provider calls, writes, or peer snapshots.
internal sealed class OwnerGoalChangeSummaryQuery(
    Func<string, CancellationToken, Task<GoalPromotionFact?>>? promotion = null,
    Func<string, string, CancellationToken, Task<CommittedGoalChanges>>? changes = null,
    Func<TaskVerificationRecord, CancellationToken, Task<string?>>? artifact = null,
    Func<GoalId, string, GoalPromotionFact?, CancellationToken, Task<OwnerGoalAcceptanceEvidence>>? acceptance = null,
    Func<string, CancellationToken, Task<string?>>? mergeBase = null)
{
    internal const int MaxArtifactBytes = 1_056_000;
    private readonly OwnerGoalRangeReadCache _cache = new(changes);
    private readonly OwnerGoalSummarySectionReader _sections = new(artifact, acceptance);

    internal static OwnerGoalChangeSummaryQuery ForWorkspace(OrchestratorWorkspace workspace) => new(
        async (id, token) =>
        {
            using var session = new SqliteReadSession(workspace.SqliteStatePath);
            return (await SqliteOrchestratorStateRepository.ReadEpicMetadataAsync(session.Connection,
                [id], false, token)).SingleOrDefault()?.PromotionFact;
        },
        (basis, candidate, token) => Task.Run(() => GoalChangesReader.ReadCommittedRange(
            workspace.RootDirectory, basis, candidate), token),
        acceptance: (id, candidate, landed, token) => Task.Run(async () =>
        {
            // A retained promotion supplies the historical gate's main even after cleanup.
            // Otherwise resolve only the selected goal's integration ref, never its dirty worktree.
            var main = landed?.CandidateRevision == candidate ? landed.PriorMainRevision : null;
            if (main is null)
            {
                var result = GitCli.Run(workspace.ExecutionDirectory, "rev-parse", "--verify",
                    $"refs/heads/{workspace.IntegrationBranch}");
                if (!result.Succeeded) throw new IOException("Integration revision unavailable.");
                main = result.Output.Trim();
            }
            token.ThrowIfCancellationRequested();
            if (!FullRevision(main)) throw new InvalidDataException("Integration revision unavailable.");
            return new OwnerGoalAcceptanceEvidence(main, await GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(
                workspace.RunEventStorePath, id, candidate, main, token));
        }, token),
        mergeBase: (candidate, token) => Task.Run(() => GoalChangesReader.ReadMergeBase(
            workspace.RootDirectory, workspace.IntegrationBranch, candidate), token));

    internal async Task<OwnerGoalChangeSummary> LoadAsync(Goal goal, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var request = _cache.BeginRangeRequest();
        GoalPromotionFact? landed = null;
        string? promotionFailure = promotion is null ? "promotion query unavailable" : null;
        try { if (promotion is not null) landed = await promotion(goal.Id.Value, token); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { promotionFailure = ex.GetType().Name; }
        if (landed is not null && landed.GoalId != goal.Id.Value)
        { landed = null; promotionFailure = "mismatched goal identity"; }

        var dispatches = goal.Tasks.SelectMany(task => task.DispatchHistory)
            .Where(item => FullRevision(item.ResultCommit)).OrderByDescending(item => item.DispatchedAt).ToArray();
        var dispatch = dispatches.FirstOrDefault();
        var candidate = landed is not null && (dispatch is null || dispatch.DispatchedAt <= landed.PromotedAt)
            ? landed.CandidateRevision : dispatch?.ResultCommit;
        candidate ??= FullRevision(goal.MetadataResultCommit) ? goal.MetadataResultCommit : null;
        // Read-only completions also record HEAD. Keep their current candidate identity, but
        // prefer a contributing range ending at that candidate over a later HEAD..HEAD range.
        var contributing = dispatches.FirstOrDefault(item =>
            string.Equals(item.ResultCommit, candidate, StringComparison.OrdinalIgnoreCase) && FullRevision(item.BaseCommit) &&
            !string.Equals(item.BaseCommit, candidate, StringComparison.OrdinalIgnoreCase));
        var promotionRange = landed is not null && landed.CandidateRevision == candidate;
        var basis = promotionRange ? landed!.PriorMainRevision : contributing?.BaseCommit ?? dispatch?.BaseCommit;
        var observedRange = changes is not null && FullRevision(basis) && FullRevision(candidate)
            ? new CommittedRange(basis!, candidate!) : null;
        _cache.ResolveRange(request, whole: false, observedRange);
        var planned = await _sections.PlannedAsync(goal, token);
        var observed = new OwnerGoalSummarySection("unavailable — no committed comparison", [], "recorded dispatch/promotion identities");
        if (observedRange is not null)
        {
            try
            {
                var facts = await _cache.SharedChangesAsync(request, observedRange, token).WaitAsync(token);
                observed = facts.Available
                    ? new(facts.TotalFiles == 0 ? "observed empty committed range" : $"observed {facts.TotalFiles} files (file scope: {FileAreas(facts.Files)})",
                        facts.Files.Take(20).Select(BoundItem).ToArray(),
                        $"Git {basis}..{candidate}; " + (promotionRange ? "promotion range" : "latest contributing dispatch range, not whole-goal coverage"),
                        Math.Max(0, facts.TotalFiles - 20), facts.Files.Take(20).Where(item => item.Length > 360).ToArray(),
                        SourceItems: facts.Files.Take(20).ToArray())
                    : new("unavailable — " + facts.Reason, [], $"Git {basis}..{candidate}");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                observed = new("unavailable — " + ex.GetType().Name, [], $"Git {basis}..{candidate}");
            }
        }
        var previewChanges = await WholeGoalChangesAsync(request, candidate, landed, promotionRange, promotionFailure, token);
        var reported = await _sections.ReportedAsync(goal, candidate, token);
        var landing = landed is null
            ? promotionFailure is null ? "landing not recorded" : "landing unavailable — " + promotionFailure
            : $"landed candidate {landed.CandidateRevision} at {landed.PromotedAt:yyyy-MM-dd HH:mm} UTC; main {landed.PromotedMainRevision}; {landed.Provenance}";
        var (verified, previewEvidence) = await _sections.VerifiedAsync(goal, candidate, landed, token);
        return new(goal.Id.Value, goal.AuthoritativeBrief.Version, EffectiveAcceptanceCriteriaVersion.ComputeForGoal(goal),
            candidate, planned, observed, reported, verified, landing)
        {
            Preview = new(OwnerGoalTitle.Full(goal.Objective), previewChanges, previewEvidence)
        };
    }

    private async Task<OwnerGoalPreviewChanges> WholeGoalChangesAsync(RangeRequest request, string? candidate, GoalPromotionFact? landed,
        bool promotionRange, string? promotionFailure, CancellationToken token)
    {
        if (candidate is null || changes is null)
        {
            _cache.ResolveRange(request, whole: true, null);
            return new(OwnerGoalPreviewChangeKind.None, [], 0);
        }
        var unavailable = new OwnerGoalPreviewChanges(OwnerGoalPreviewChangeKind.Unavailable, [], 0);
        try
        {
            // Promotion failures cannot safely be treated as unlanded goals: main may already contain the candidate.
            if ((landed is not null && !promotionRange) || (promotion is not null && promotionFailure is not null))
            {
                _cache.ResolveRange(request, whole: true, null);
                return unavailable;
            }
            var basis = landed is not null ? landed.PriorMainRevision :
                mergeBase is not null ? await mergeBase(candidate, token).WaitAsync(token) : null;
            if (!FullRevision(basis) || !FullRevision(candidate))
            {
                _cache.ResolveRange(request, whole: true, null);
                return unavailable;
            }
            // Applicability is resolved anew before sharing immutable committed range facts.
            var range = new CommittedRange(basis!, candidate);
            _cache.ResolveRange(request, whole: true, range);
            var facts = await _cache.SharedChangesAsync(request, range, token).WaitAsync(token);
            if (!facts.Available) return unavailable;
            return new(facts.TotalFiles == 0 ? OwnerGoalPreviewChangeKind.None : OwnerGoalPreviewChangeKind.Listed,
                facts.Files, facts.TotalFiles);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _cache.ResolveRange(request, whole: true, null);
            return unavailable;
        }
    }
}
