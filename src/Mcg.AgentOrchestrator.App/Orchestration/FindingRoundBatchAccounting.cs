using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.Orchestration;

// Timeline records are the durable source for membership, terminal settlement and selection witnesses.
// Savings are never inferred from the number of requests or from polling frequency.
internal static class FindingRoundBatchAccounting
{
    internal sealed record Member(string RequestIdentity, string[] FindingIds);
    internal sealed record Observation(string BatchKey, string Kind, string Identity, string[] FindingIds,
        string? ReceiptId = null, AgentRole? Role = null, string? Eligibility = null);
    internal sealed record Summary(int BatchSize, int NewlyExecuted, int Covered, int Unavailable,
        int CoalescedSelectionCount, int TesterCallsAvoided, int ReviewerCallsAvoided,
        int UnknownSelections, int? PaidVerificationCallsAvoided, string RoutingReason, Observation[] EligibilityWitnesses,
        int Superseded = 0, int Deferred = 0)
    {
        internal bool IsSettled => NewlyExecuted + Covered + Unavailable + Superseded + Deferred == BatchSize;
    }
    private const string Prefix = "finding-evidence batch-observation ";

    internal static Member[] Members(IEnumerable<ReviewFinding> findings) => findings
        .Where(finding => finding.EvidenceRequest is not null)
        .GroupBy(finding => ConductorDriver.BuildFindingEvidenceIdentity(finding.EvidenceRequest!))
        .Select(group => new Member(group.Key, group.Select(finding => finding.StableId).Distinct().ToArray())).ToArray();

    internal static Observation[] Read(Goal goal, FindingEvidenceBatchIdentity identity) => goal.Timeline
        .Where(item => item.TaskId == identity.RequestingTaskId && item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
            item.Message.StartsWith(Prefix, StringComparison.Ordinal))
        .Select(item => JsonSerializer.Deserialize<Observation>(item.Message[Prefix.Length..]) ??
            throw new InvalidOperationException("Finding batch observation is null."))
        .Where(item => item.BatchKey == identity.Key).ToArray();

    internal static void Record(Goal goal, FindingEvidenceBatchIdentity identity, Observation observation,
        Action<GoalId, TaskId, string> record)
    {
        lock (goal)
        {
            if (Read(goal, identity).Any(item => item.Kind == observation.Kind && item.Identity == observation.Identity)) return;
            record(goal.Id, identity.RequestingTaskId, Prefix + JsonSerializer.Serialize(observation));
        }
    }

    internal static void CheckpointRun(Goal goal, TaskId taskId, string message, DateTimeOffset occurredAt,
        Action checkpoint, Action<ProgressEvent> writeEvent)
    {
        // The run allowance is read from the goal snapshot after a restart.
        var progress = new ProgressEvent(goal.Id, taskId, ProgressKind.FindingEvidenceRunRecorded, message, occurredAt);
        goal.Append(progress);
        try { checkpoint(); }
        catch
        {
            goal.RemoveStagedProgress(progress);
            throw;
        }
        writeEvent(progress);
    }

    internal static void Begin(Goal goal, FindingEvidenceBatchIdentity identity, IEnumerable<ReviewFinding> findings,
        Action<GoalId, TaskId, string> record)
    {
        foreach (var member in Members(findings))
            Record(goal, identity, new(identity.Key, "member", member.RequestIdentity, member.FindingIds), record);
    }

    internal static bool HasOpenBatch(Goal goal, FindingEvidenceBatchIdentity identity)
    {
        lock (goal)
            return Read(goal, identity).Any(item => item.Kind == "member") && !IsClosed(goal, identity);
    }

    internal static bool IsClosed(Goal goal, FindingEvidenceBatchIdentity identity)
    {
        // Admission stages a marker that can still be rolled back if its effect fails.
        var marker = $"finding-evidence batch-closed; batch_key={identity.Key};";
        lock (goal)
            return goal.Timeline.Any(item =>
                item.TaskId == identity.RequestingTaskId && item.Kind == ProgressKind.FindingEvidenceRequestRecorded &&
                item.Message.StartsWith(marker, StringComparison.Ordinal));
    }

    internal static Observation[] RequiredUnavailable(Goal goal, TaskSpec task, FindingEvidenceBatchIdentity identity)
    {
        var blockingIds = RequiredFindingIds(goal, task);
        return Read(goal, identity)
            .Where(item => item.Kind == "unavailable" && item.FindingIds.Any(blockingIds.Contains)).ToArray();
    }

    internal static HashSet<string> RequiredFindingIds(Goal goal, TaskSpec task) =>
        ReviewFindings.GetOpenBlockingFindings(task.LastVerification?.MergedReviewFindings ?? [],
            goal.EffectiveAcceptanceCriteriaCorrections).Select(finding => finding.StableId).ToHashSet(StringComparer.Ordinal);

    internal static HashSet<string> NonExecutableFindingIds(Goal goal, FindingEvidenceBatchIdentity identity) =>
        Read(goal, identity).Where(item => item.Kind is "unavailable" or "superseded")
            .SelectMany(item => item.FindingIds).ToHashSet(StringComparer.Ordinal);

    internal static Summary Summarize(Goal goal, TaskSpec task, FindingEvidenceBatchIdentity identity, string reason)
    {
        var observations = Read(goal, identity);
        var members = observations.Where(item => item.Kind == "member").DistinctBy(item => item.Identity).ToArray();
        var executed = 0;
        var covered = 0;
        var unavailable = 0;
        var superseded = 0;
        var deferred = 0;
        foreach (var member in members)
        {
            var outcomes = observations.Where(item => item.FindingIds.Intersect(member.FindingIds).Any()).ToArray();
            if (outcomes.Any(item => item.Kind == "unavailable")) unavailable++;
            else if (outcomes.Any(item => item.Kind == "executed")) executed++;
            else if (outcomes.Any(item => item.Kind == "covered")) covered++;
            else if (outcomes.Any(item => item.Kind == "superseded")) superseded++;
            else if (outcomes.Any(item => item.Kind == "deferred")) deferred++;
        }
        var selections = observations.Where(item => item.Kind == "suppressed-selection").DistinctBy(item => item.Identity).ToArray();
        return new(members.Length, executed, covered, unavailable,
            selections.Count(item => item.Role is not null),
            selections.Count(item => item.Role == AgentRole.Tester),
            selections.Count(item => item.Role == AgentRole.Reviewer),
            selections.Count(item => item.Role is null),
            selections.Any(item => item.Role is null) ? null : selections.Count(item => item.Role is AgentRole.Tester or AgentRole.Reviewer),
            reason, selections, superseded, deferred);
    }
}
