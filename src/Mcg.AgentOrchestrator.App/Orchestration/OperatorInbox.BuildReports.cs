using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static partial class OperatorInbox
{
    // Decode only the established conductor wait serialization, never arbitrary prose mentioning a request.
    internal static bool IsAnsweredBuildReportReason(string reason, IReadOnlyList<HumanInputRequest> requests)
    {
        var match = Regex.Match(reason,
            @"\Aacceptance passed at [a-f0-9]{8,40}; (?<count>[1-9][0-9]*) pending human wait\(s\) \(ProspectiveAcceptanceEvidence (?<waits>[a-f0-9]{8}(?:, [a-f0-9]{8})*)\)\z");
        if (!match.Success) return false;
        var prefixes = match.Groups["waits"].Value.Split(", ", StringSplitOptions.None);
        if (!int.TryParse(match.Groups["count"].Value, out var count) || count != prefixes.Length ||
            prefixes.Distinct(StringComparer.Ordinal).Count() != count) return false;
        return prefixes.All(prefix =>
        {
            var exact = requests.Where(request => request.Id.Value.StartsWith(prefix, StringComparison.Ordinal)).ToArray();
            return exact.Length == 1 && exact[0].IsCompleted &&
                exact[0].Kind == HumanWaitKind.ProspectiveAcceptanceEvidence && exact[0].EvidenceOwner == "developer" &&
                exact[0].PlannerIdentity is { } identity &&
                exact[0].AuthoritativeAnswer is { Origin: HumanInputAnswerOrigin.Worker } receipt && receipt.BriefVersion == identity.BriefVersion &&
                exact[0].Answer is { } answer && answer.StartsWith("ROLE_REPORT_EVIDENCE owner=developer ", StringComparison.Ordinal) &&
                answer.Contains(" build_invocations=", StringComparison.Ordinal);
        });
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
        var resolvedReasons = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (row.GoalId != goal.Id.Value || row.Type != CollaborationItemType.Decision ||
                !IsAnsweredBuildReportReason(row.Body, ownRequests)) continue;
            var key = BuildId(goal.Id, OperatorInboxKind.LandingEscalation, $"landing-escalation:{goal.Id.Value}:{row.Body}");
            if (row.CorrelationKey != key || rows.Count(item => item.CorrelationKey == key) != 1) continue;
            const string resolution = "Stored Developer build report answered the correlated evidence wait.";
            if (!CollaborationItemLifecycle.IsTerminal(row.Status) &&
                !store.TryResolveAsync(key, resolution).GetAwaiter().GetResult())
                throw new InvalidOperationException($"Build report attention resolution has no exact store receipt: {row.Id}; retry reconciliation.");
            resolvedReasons.Add(row.Body);
        }
        // Keep the persisted occurrence retryable until its exact collaboration row has a terminal receipt.
        var items = LoadLandingEscalationsUnsafe(workspace).ToArray();
        var changed = false;
        for (var index = 0; index < items.Length; index++)
        {
            var item = items[index];
            if (item.GoalId != goal.Id.Value || item.ResolvedAtUtc is not null || item.AttributionPendingIdentity is not null ||
                !resolvedReasons.Contains(item.Reason)) continue;
            items[index] = item with { ResolvedAtUtc = DateTimeOffset.UtcNow, ResolvedBy = "developer-build-report",
                ResolutionReason = "Stored Developer build report answered the correlated evidence wait." };
            changed = true;
        }
        if (changed) SaveLandingEscalationsUnsafe(workspace, items);
    }
}
