using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Summarizes persisted starts for one goal; required-event identity owns deduplication.
internal static class PreReviewEvidenceAttemptPileUp
{
    internal const int AttemptCountWarningThreshold = 4;
    internal const string EventKind = "EVIDENCE_ATTEMPTS_HIGH";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static string EventId(string goalId) => $"evidence:{goalId}:attempts-high";

    internal static void WarnIfHigh(
        ConductEventLogWriter? writer,
        ConductorParallelAcceptanceAttempt startedAttempt,
        DateTimeOffset observedAt)
    {
        try
        {
            if (writer is null ||
                startedAttempt.Kind != ConductorParallelAcceptanceAttemptCoordinator.PreReviewEvidenceDispatchKind)
                return;

            var summary = Summarize(Path.GetDirectoryName(startedAttempt.MetadataPath)!, startedAttempt, observedAt);
            if (summary.AttemptCount >= AttemptCountWarningThreshold)
                writer.AppendRequired(EventKind, startedAttempt.GoalId, summary.EventDetail(startedAttempt.GoalId),
                    observedAt, EventId(startedAttempt.GoalId));
        }
        catch (Exception)
        {
            // Advisory reporting must never turn a successful start into a launch failure.
        }
    }

    internal static PreReviewEvidenceAttemptSummary Summarize(
        string goalDirectory,
        ConductorParallelAcceptanceAttempt startedAttempt,
        DateTimeOffset observedAt)
    {
        var attempts = new Dictionary<string, ConductorParallelAcceptanceAttempt>(StringComparer.Ordinal);
        var unreadable = 0;
        if (Directory.Exists(goalDirectory))
        {
            foreach (var path in Directory.EnumerateFiles(goalDirectory)
                         .Where(path => path.EndsWith(".attempt.json", StringComparison.OrdinalIgnoreCase))
                         .Order(StringComparer.Ordinal))
            {
                ConductorParallelAcceptanceAttempt? attempt;
                try
                {
                    attempt = ReadAttempt(path);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    unreadable++;
                    continue;
                }

                if (attempt is null || string.IsNullOrWhiteSpace(attempt.AttemptId) ||
                    !string.Equals(attempt.GoalId, startedAttempt.GoalId, StringComparison.Ordinal) ||
                    (!string.IsNullOrWhiteSpace(attempt.Kind) &&
                     attempt.Kind != ConductorParallelAcceptanceAttemptCoordinator.GateDispatchKind &&
                     attempt.Kind != ConductorParallelAcceptanceAttemptCoordinator.PreReviewEvidenceDispatchKind))
                {
                    unreadable++;
                    continue;
                }

                if (attempt.Kind == ConductorParallelAcceptanceAttemptCoordinator.PreReviewEvidenceDispatchKind)
                    attempts.TryAdd(attempt.AttemptId, attempt);
            }
        }

        var totalElapsed = TimeSpan.Zero;
        foreach (var attempt in attempts.Values)
        {
            var end = attempt.CompletedAt ??
                (attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.Running
                    ? observedAt
                    : attempt.ReconciledAt ?? attempt.LastHeartbeatAt);
            if (end > attempt.StartedAt)
                totalElapsed += end - attempt.StartedAt;
        }

        var causes = attempts.Values
            .Where(attempt => attempt.AttemptId != startedAttempt.AttemptId &&
                (!string.IsNullOrWhiteSpace(attempt.SupersededBy) || attempt.SupersessionCause is not null ||
                 attempt.Outcome == ConductorParallelAcceptanceAttemptOutcome.StaleCandidate))
            .OrderBy(attempt => attempt.Ordinal > 0 ? attempt.Ordinal : int.MaxValue)
            .ThenBy(attempt => attempt.StartedAt)
            .ThenBy(attempt => attempt.AttemptId, StringComparer.Ordinal)
            .Select(attempt => CauseToken(attempt.SupersessionCause))
            .ToArray();
        return new(attempts.Count, unreadable, totalElapsed, causes);
    }

    private static ConductorParallelAcceptanceAttempt? ReadAttempt(string path)
    {
        for (var retry = 0; ; retry++)
        {
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return JsonSerializer.Deserialize<ConductorParallelAcceptanceAttempt>(stream, JsonOptions);
            }
            catch (IOException ex) when ((ex.HResult & 0xFFFF) is 32 or 33 && retry < 5)
            {
                Thread.Sleep(TimeSpan.FromMilliseconds(25 * (retry + 1)));
            }
        }
    }

    private static string CauseToken(ConductorEvidenceSupersessionCause? cause)
    {
        if (cause is null || !Enum.IsDefined(cause.Value))
            return "unknown";

        var name = cause.Value.ToString();
        var token = new StringBuilder();
        for (var i = 0; i < name.Length; i++)
        {
            if (i > 0 && char.IsUpper(name[i]))
                token.Append('_');
            token.Append(char.ToLowerInvariant(name[i]));
        }
        return token.ToString();
    }
}

internal sealed record PreReviewEvidenceAttemptSummary(
    int AttemptCount,
    int UnreadableCount,
    TimeSpan TotalElapsed,
    IReadOnlyList<string> PriorSupersessionCauses)
{
    internal string EventDetail(string goalId)
    {
        var seconds = Math.Max(0, (long)TotalElapsed.TotalSeconds);
        var elapsed = seconds >= 3600
            ? FormattableString.Invariant($"{seconds / 3600}h{seconds % 3600 / 60}m{seconds % 60}s")
            : FormattableString.Invariant($"{seconds / 60}m{seconds % 60}s");
        var causes = PriorSupersessionCauses.Count == 0 ? "none" : string.Join(',', PriorSupersessionCauses);
        return string.Create(CultureInfo.InvariantCulture,
            $"goal={goalId} attempts={AttemptCount} threshold={PreReviewEvidenceAttemptPileUp.AttemptCountWarningThreshold} unreadable={UnreadableCount} total_elapsed={elapsed} causes={causes}") +
            (UnreadableCount > 0 ? " The true attempt count may be higher." : "");
    }
}
