namespace Mcg.AgentOrchestrator.App.Orchestration;

// A metadata-only pass. Unsupported history must not cause result/TRX reads or evidence copies.
internal sealed class ExperimentReplayImportGateFilter(
    IReadOnlyList<ExperimentReplayCorpusImporter.Round> rounds, DateTimeOffset cutoff, List<string> diagnostics,
    CancellationToken token)
{
    internal Dictionary<string, AcceptanceDeveloperRoundLink> Links { get; } = new(StringComparer.Ordinal);
    private readonly Dictionary<ExperimentReplayCorpusImporter.Round, string> _unavailable = [];

    internal bool Include(ConductorParallelAcceptanceAttempt attempt, string path)
    {
        token.ThrowIfCancellationRequested();
        if (attempt.StartedAt > cutoff) return false;
        var candidates = rounds.Where(r => r.GoalId == attempt.GoalId && r.CandidateCommit == attempt.BranchHeadSha &&
            attempt.StartedAt >= r.CompletedAt).ToArray();
        if (candidates.Length == 0) return false;
        var link = attempt.DeveloperRoundLink;
        ExperimentReplayCorpusImporter.Round[] matched = link is null ? [] : candidates.Where(r =>
            link.GoalId == r.GoalId && link.TaskId == r.TaskId && link.DispatchedAt == r.DispatchAt &&
            link.BaseCommit == r.BaseCommit && link.CandidateCommit == r.CandidateCommit &&
            link.ObservedMain == r.ObservedMain && link.ObservedMain == attempt.MainHeadSha &&
            (attempt.ProjectName is null || attempt.ProjectName == r.Project)).ToArray();
        if (matched.Length == 1)
        {
            Links.Add(path, link!);
            return true;
        }
        var reason = link is null ? "unsupported-history: authoritative task/dispatch linkage was not recorded"
            : "unsupported-provenance: gate dispatch link does not match one pinned Developer round";
        foreach (var round in candidates) _unavailable.TryAdd(round, reason);
        diagnostics.Add($"{attempt.AttemptId}: {reason}");
        return false;
    }

    internal ExperimentReplayCorpusImporter.Round Annotate(ExperimentReplayCorpusImporter.Round round) =>
        _unavailable.TryGetValue(round, out var reason) ? round with { LinkageUnavailableReason = reason } : round;
}
