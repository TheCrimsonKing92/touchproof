namespace Mcg.AgentOrchestrator.Infrastructure;

internal sealed partial class AcceptancePartitionVerdictCache
{
    // A session discovers sources once, only when closure fallback is proposed.
    // The version-1 index carries an attempt and artifacts, but no owning goal or exact-pair key.
    private IReadOnlyList<(string Path, PartitionVerdictJournalSnapshot Journal)>? _sourceJournals;

    private static PartitionVerdictRecord? LatestVerdict(
        PartitionVerdictJournalSnapshot journal, string goalId, string cacheKey) =>
        journal.Records.LastOrDefault(record =>
            record.GoalId.Equals(goalId, StringComparison.OrdinalIgnoreCase) &&
            record.CacheKey.Equals(cacheKey, StringComparison.OrdinalIgnoreCase));

    private bool CanConsiderReuse(
        PartitionVerdictJournalSnapshot journal, PartitionVerdictRecord? latest,
        string partitionId, string? closureHash = null)
    {
        // Absence permits fallback. Known nonreusable evidence does not.
        if (journal.IsComplete && (latest is null || IsReusableExecution(journal, latest)))
            return true;
        RecordMiss(partitionId, PartitionVerdictMissReasons.NonreusableAuthoritativeVerdict, closureHash);
        return false;
    }

    private static bool IsReusableExecution(
        PartitionVerdictJournalSnapshot journal, PartitionVerdictRecord latest)
    {
        if (!journal.IsComplete || !latest.Passed)
            return false;
        if (!journal.Records.Any(record => !record.Passed &&
                record.GoalId.Equals(latest.GoalId, StringComparison.OrdinalIgnoreCase) &&
                record.CacheKey.Equals(latest.CacheKey, StringComparison.OrdinalIgnoreCase)))
            return true;

        // Legacy greens remain supported, but superseding a known failure requires
        // affirmative executed, passing coverage, even when structural enforcement is off.
        if (latest.TestResultPaths.Count == 0 ||
            latest.TestResultPaths.Any(path => AcceptanceBestEffortFileOperations.TryGetFileLength(path) <= 0))
            return false;
        var completion = AcceptanceShardCompletionAdjudicator.InspectTrxCompletion(latest.TestResultPaths);
        return completion.Passed && completion.ExecutedTestCount > 0 &&
            AcceptanceShardCompletionAdjudicator.DecideTestShard(false, 0, completion).Passed;
    }

    private bool HasAuthoritativeClosureSource(AcceptanceClosureVerdictRecord proposal, string partitionId)
    {
        var sources = SourceJournals()
            .SelectMany(item => item.Journal.Records
                .Where(record => record.AttemptId.Equals(proposal.SourceAttemptId, StringComparison.Ordinal) &&
                    record.PartitionId.Equals(partitionId, StringComparison.OrdinalIgnoreCase) &&
                    record.PartitionFilterHash.Equals(proposal.PartitionFilterHash, StringComparison.OrdinalIgnoreCase))
                .Select(record => (item.Path, Record: record)))
            .ToArray();
        if (sources.Length == 1)
        {
            var (path, source) = sources[0];
            var expectedKey = string.Join(':', source.GoalId, source.CandidateTreeSha,
                source.MainSha, proposal.ManifestIdentity, proposal.PartitionFilterHash);
            if (source.CacheKey.Equals(expectedKey, StringComparison.OrdinalIgnoreCase) &&
                proposal.TestResultPaths is not null &&
                source.TestResultPaths.SequenceEqual(proposal.TestResultPaths, StringComparer.Ordinal) &&
                (string.Equals(source.ClosureHash, proposal.ClosureHash, StringComparison.Ordinal) ||
                    source.ClosureHash is null && !AcceptanceWholeProjectClosureReuse.AppliesTo(partitionId)) &&
                SourceIsStillAuthoritative(path, source))
                return true;
        }

        RecordMiss(partitionId, PartitionVerdictMissReasons.ClosureSourceNotAuthoritative, proposal.ClosureHash);
        return false;
    }

    private static bool SourceIsStillAuthoritative(string path, PartitionVerdictRecord source)
    {
        try
        {
            // Discovery is derived; validate the owning journal at the point of reuse.
            var journal = ReadPartitionVerdictJournal(path);
            var latest = LatestVerdict(journal, source.GoalId, source.CacheKey);
            return latest is not null && latest.AttemptId.Equals(source.AttemptId, StringComparison.Ordinal) &&
                latest.TestResultPaths.SequenceEqual(source.TestResultPaths, StringComparer.Ordinal) &&
                IsReusableExecution(journal, latest);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private IReadOnlyList<(string Path, PartitionVerdictJournalSnapshot Journal)> SourceJournals()
    {
        lock (_gate)
        {
            if (_sourceJournals is not null) return _sourceJournals;
            try
            {
                var directory = Path.GetDirectoryName(JournalPath)!;
                _sourceJournals = Directory.Exists(directory)
                    ? Directory.EnumerateFiles(directory, "*.jsonl")
                        .Select(path => (path, ReadPartitionVerdictJournal(path))).ToArray()
                    : [];
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The derived index cannot supply missing execution authority.
                _sourceJournals = [];
            }
            return _sourceJournals;
        }
    }
}
