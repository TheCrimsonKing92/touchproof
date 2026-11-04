using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class OperatorInbox
{
    // Decode only the established conductor wait serialization, never arbitrary prose mentioning a request.
    private static bool TryRemoveAnsweredBuildReportWaits(string reason, IReadOnlyList<HumanInputRequest> requests,
        out string? remainingReason)
    {
        remainingReason = null;
        var match = Regex.Match(reason,
            @"\A(?<acceptance>acceptance passed at [a-f0-9]{8,40})(?<verification>; landing held by [1-9][0-9]* open verification\(s\))?; " +
            @"(?<count>[1-9][0-9]*) pending human wait\(s\) \((?<waits>[A-Za-z]+ [a-f0-9]{8}(?:, [a-f0-9]{8})*(?:; [A-Za-z]+ [a-f0-9]{8}(?:, [a-f0-9]{8})*)*)\)" +
            @"(?<blockers>; [1-9][0-9]* blocker\(s\) \((?<kind>[A-Za-z]+)(?:, (?<kind>[A-Za-z]+))*(?:, \+[1-9][0-9]* more)?\))?\z");
        if (!match.Success) return false;
        if (match.Groups["kind"].Captures.Any(capture =>
            !Enum.TryParse<GoalAcceptanceBlockerKind>(capture.Value, out var kind) || !Enum.IsDefined(kind))) return false;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var remainingGroups = new List<string>();
        var remainingCount = 0;
        var removed = false;
        foreach (var group in match.Groups["waits"].Value.Split("; ", StringSplitOptions.None))
        {
            var separator = group.IndexOf(' ');
            if (!Enum.TryParse<HumanWaitKind>(group[..separator], out var kind) || !Enum.IsDefined(kind)) return false;
            var remaining = new List<string>();
            foreach (var prefix in group[(separator + 1)..].Split(", ", StringSplitOptions.None))
            {
                var exact = requests.Where(request => request.Id.Value.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
                if (!seen.Add(prefix) || exact.Length != 1 || exact[0].Kind != kind) return false;
                var request = exact[0];
                if (request.IsCompleted && kind == HumanWaitKind.ProspectiveAcceptanceEvidence && request.EvidenceOwner == "developer" &&
                    request.PlannerIdentity is { } identity &&
                    request.AuthoritativeAnswer is { Origin: HumanInputAnswerOrigin.Worker } receipt && receipt.BriefVersion == identity.BriefVersion &&
                    request.Answer is { } answer && answer.StartsWith("ROLE_REPORT_EVIDENCE owner=developer ", StringComparison.Ordinal) &&
                    answer.Contains(" build_invocations=", StringComparison.Ordinal))
                    removed = true;
                else
                    remaining.Add(prefix);
            }
            remainingCount += remaining.Count;
            if (remaining.Count > 0) remainingGroups.Add($"{kind} {string.Join(", ", remaining)}");
        }
        if (!removed || !int.TryParse(match.Groups["count"].Value, out var count) || count != seen.Count) return false;
        if (remainingCount > 0 || match.Groups["verification"].Success || match.Groups["blockers"].Success)
            remainingReason = match.Groups["acceptance"].Value + match.Groups["verification"].Value +
                (remainingCount > 0 ? $"; {remainingCount} pending human wait(s) ({string.Join("; ", remainingGroups)})" : "") +
                match.Groups["blockers"].Value;
        return true;
    }

    internal static void ResolveAnsweredBuildReportEscalations(OrchestratorWorkspace workspace, Goal goal,
        IReadOnlyList<HumanInputRequest> requests, ICollaborationItemStore? collaborationStore = null)
    {
        var ownRequests = requests.Where(request => request.GoalId == goal.Id).ToArray();
        if (!ownRequests.Any(request => request.IsCompleted && request.Answer?.Contains(" build_invocations=", StringComparison.Ordinal) == true))
            return;
        using var escalationLock = AcquireLandingEscalationLock(workspace);
        var store = collaborationStore ?? CollaborationItemStore.ForDirectory(workspace.OrchestratorDirectory);
        var rows = store.ListAsync(goal.Id.Value).GetAwaiter().GetResult();
        const string resolution = "Stored Developer build report answered the correlated evidence wait.";
        var resolvedReasons = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var occurrence in rows.GroupBy(row => row.CorrelationKey))
        {
            // Terminal history must not make a single active recurrence ambiguous. A terminal receipt
            // from this reconciler also permits retrying a partially completed persisted-file update.
            var active = occurrence.Where(row => !CollaborationItemLifecycle.IsTerminal(row.Status)).ToArray();
            var row = active.Length == 1 ? active[0] : active.Length == 0
                ? occurrence.FirstOrDefault(row => row.Resolution == resolution) : null;
            if (row is null || row.GoalId != goal.Id.Value || row.Type != CollaborationItemType.Decision ||
                !TryRemoveAnsweredBuildReportWaits(row.Body, ownRequests, out var remainingReason)) continue;
            var key = BuildId(goal.Id, OperatorInboxKind.LandingEscalation, $"landing-escalation:{goal.Id.Value}:{row.Body}");
            if (row.CorrelationKey != key) continue;
            // Publish the remaining demand before retiring the stale occurrence. Never discard another
            // wait or blocker, and never resurrect a replacement that has already been handled.
            if (remainingReason is not null)
            {
                var remainingKey = BuildId(goal.Id, OperatorInboxKind.LandingEscalation,
                    $"landing-escalation:{goal.Id.Value}:{remainingReason}");
                if (!rows.Any(item => item.CorrelationKey == remainingKey))
                    store.RaiseAsync(CollaborationItemType.Decision, goal.Id.Value, row.Subject, remainingReason, remainingKey)
                        .GetAwaiter().GetResult();
            }
            if (!CollaborationItemLifecycle.IsTerminal(row.Status) &&
                !store.TryResolveAsync(key, resolution).GetAwaiter().GetResult())
                throw new InvalidOperationException($"Build report attention resolution has no exact store receipt: {row.Id}; retry reconciliation.");
            resolvedReasons[row.Body] = remainingReason;
        }
        // Keep the persisted occurrence retryable until its exact collaboration row has a terminal receipt.
        var items = LoadLandingEscalationsUnsafe(workspace).ToList();
        var changed = false;
        var originalCount = items.Count;
        for (var index = 0; index < originalCount; index++)
        {
            var item = items[index];
            if (item.GoalId != goal.Id.Value || item.ResolvedAtUtc is not null || item.AttributionPendingIdentity is not null ||
                !resolvedReasons.TryGetValue(item.Reason, out var remainingReason)) continue;
            items[index] = item with { ResolvedAtUtc = DateTimeOffset.UtcNow, ResolvedBy = "developer-build-report",
                ResolutionReason = resolution };
            if (remainingReason is not null && !items.Any(other => other.GoalId == goal.Id.Value && other.Reason == remainingReason))
                items.Add(item with { Reason = remainingReason, EscalatedAt = DateTimeOffset.UtcNow });
            changed = true;
        }
        if (changed) SaveLandingEscalationsUnsafe(workspace, items);
    }
}
