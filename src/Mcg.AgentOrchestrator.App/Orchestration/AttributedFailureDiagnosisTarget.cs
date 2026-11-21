using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal static class AttributedFailureDiagnosisTarget
{
    private const string RouteMarker = "ATTRIBUTED_FAILURE_ROUTE ";
    private const string FailingTestsPrefix = "failing_tests=";
    private const string LegacyFinding = "**The candidate didn't cause these failures, and they don't show anything wrong with it.**";
    private const string ExplicitFinding = "The candidate did not cause these failures.";
    // The operator supplied this specific legacy note as conclusive evidence (goal cfda6f88, event 149).
    // Fingerprint its header and entire body with LF line endings. Other free-form advice can retract an opening claim.
    private const string ReviewedLegacyNoteHash = "FFB6A57A4115B66A80409806C60B568F97E059E2532DE9302C5BFC0D98A98514";
    private static readonly Regex RouteHeaderPattern = new(
        @"\AATTRIBUTED_FAILURE_ROUTE source=(?<source>\S+) candidate_sha=(?<candidate>\S+) receipt_id=(?<receipt>\S+) signature=(?<signature>\S+)\z");

    internal static TaskSpec? Select(Goal goal, TaskSpec routed)
    {
        if (routed is { Status: WorkTaskStatus.Completed, LastDispatch: not null }) return routed;

        // The stable sort keeps the later task and verification history entries first on ties.
        var completions = from task in goal.Tasks.Reverse()
                          where task.RequiredRole == AgentRole.Developer
                          from verification in task.VerificationHistory.Reverse()
                          where verification.EvidenceSource != VerificationEvidenceSource.Manual
                          orderby verification.CompletedAt descending
                          select (task, verification);
        foreach (var (task, verification) in completions)
        {
            if (FindDispatch(task, verification) is not { } dispatch) continue;
            // InquiryDispatcher reads the parent from LastDispatch. Reconstruct only that view
            // so a historical inquiry never changes the task's live attempt or status.
            var target = TaskSpec.FromSnapshot(new TaskSnapshot(
                task.Id.Value, task.Description, task.RequiredRole, WorkTaskStatus.Completed,
                AssignedAgentId: null, LastExecution: null, LastVerification: null,
                VerificationHistory: null, LastDispatch: null, LastProcess: null));
            target.RecordDispatch(dispatch);
            return target;
        }
        return null;
    }

    private static TaskDispatchRecord? FindDispatch(TaskSpec task, TaskVerificationRecord verification)
    {
        if (verification.DispatchAttemptAt is { } attempt)
            return task.DispatchHistory.LastOrDefault(dispatch =>
                dispatch.DispatchedAt == attempt && attempt <= verification.CompletedAt);

        if (verification.DispatchStartedAt is { } started && started <= verification.CompletedAt)
        {
            // Older results record the process start, which belongs to the attempt open at that time.
            var dispatch = task.DispatchHistory.LastOrDefault(item => item.DispatchedAt <= started);
            return dispatch is not null && MatchesExecution(dispatch, verification) ? dispatch : null;
        }

        if (verification.DispatchStartedAt is not null || verification.EvidenceSource != VerificationEvidenceSource.Dispatch)
            return null;
        var attempts = task.DispatchHistory.Where(dispatch => dispatch.DispatchedAt <= verification.CompletedAt &&
            MatchesExecution(dispatch, verification)).Take(2).ToArray();
        return attempts.Length == 1 ? attempts[0] : null;
    }

    private static bool MatchesExecution(TaskDispatchRecord dispatch, TaskVerificationRecord verification) =>
        string.Equals(dispatch.Command, verification.Command, StringComparison.Ordinal) &&
        string.Equals(dispatch.WorkingDirectory, verification.WorkingDirectory, StringComparison.OrdinalIgnoreCase);

    internal static ConductorAdvanceResult? TryHold(Goal goal, string prefix, ConductorAutonomyPolicy policy,
        AttributedFailureRouteDecision route, Action<Goal, GoalLifecycleState, string> recordEscalation)
    {
        if (route.DiagnosisUnavailableReason != AttributedFailureDiagnosisUnavailableReason.NoCompletedDeveloperDispatch ||
            route.Diagnosis is not { } current || Normalize(route.Failure?.FailingTests) is not { } identities)
            return null;

        var prior = FindPriorDiagnosis(goal, current, identities);
        if (prior is not { } match) return null;

        var reference = $"task={match.Event.TaskId!.Value} occurred_at={match.Event.OccurredAt:O} " +
            $"candidate_sha={match.Candidate} signature={match.Signature}";
        if (match.Receipt is not null) reference += $" receipt_id={match.Receipt}";
        var reason = $"Operator review required. Earlier attributed-failure diagnosis ({reference}) found the candidate not at fault. " +
            $"Finding: {match.Finding}";
        recordEscalation(goal, GoalLifecycleState.AcceptanceFailed, reason);
        return new(goal.Id.Value, prefix, policy.Name, new ConductorAdvanceOutcome.Held(GoalLifecycleState.AcceptanceFailed, reason)
        {
            Owner = ConductorHoldOwner.Operator,
            Decision = new("acceptance", "Hold", 1, AttributedFailureRouteDecision.NoCompletedDeveloperDispatchCode, reason,
                [new("prior_diagnosis", reference), new("prior_finding", match.Finding)])
        });
    }

    private static (ProgressEvent Event, string Candidate, string Signature, string? Receipt, string Finding)? FindPriorDiagnosis(
        Goal goal, AttributedFailureDiagnosisOutcome current, HashSet<string> identities)
    {
        var history = goal.Timeline;
        var end = history.Count;
        for (var i = 0; i < history.Count; i++)
        {
            if (ReadDiagnosis(goal.Id, history[i]) is not { } header ||
                header.Groups["candidate"].Value != current.CandidateSha ||
                header.Groups["signature"].Value != current.Signature ||
                header.Groups["status"].Value != current.Status) continue;
            // On replay, only evidence already recorded before this diagnosis can authorize the hold.
            end = i;
            break;
        }

        var routes = new Dictionary<(TaskId, string, string), (HashSet<string> Tests, string Receipt)?>();
        for (var i = end - 1; i >= 0; i--)
        {
            var entry = history[i];
            if (entry.GoalId != goal.Id || entry.TaskId is not { } taskId) continue;
            if (entry.Kind == ProgressKind.TaskRetried && entry.Message.StartsWith(RouteMarker, StringComparison.Ordinal))
            {
                var lines = AttributedFailureDiagnosisOutcome.Lines(entry.Message);
                var route = RouteHeaderPattern.Match(lines[0]);
                if (!route.Success) continue;
                var key = (taskId, route.Groups["candidate"].Value, route.Groups["signature"].Value);
                var tests = ReadLegacyIdentities(route, lines);
                if (!routes.TryAdd(key, tests is null ? null : (tests, route.Groups["receipt"].Value)))
                    routes[key] = null;
                continue;
            }

            if (ReadDiagnosis(goal.Id, entry) is not { } diagnosis) continue;
            var candidate = diagnosis.Groups["candidate"].Value;
            var signature = diagnosis.Groups["signature"].Value;
            routes.Remove((taskId, candidate, signature), out var legacy);
            var encoded = diagnosis.Groups["identities"];
            var priorTests = encoded.Success ? ReadIdentities(encoded.Value) : legacy?.Tests;
            if (priorTests is null || !identities.SetEquals(priorTests)) continue;

            // Only the latest matching record controls, regardless of its conclusion.
            if (diagnosis.Groups["status"].Value != "cause-found") return null;
            var advice = AttributedFailureDiagnosisOutcome.Lines(entry.Message);
            var finding = advice switch
            {
                [_, "| " + ExplicitFinding] => ExplicitFinding,
                [_, "| " + LegacyFinding] => LegacyFinding,
                _ when legacy is not null && IsReviewedLegacyNote(advice) => LegacyFinding,
                _ => null
            };
            return finding is null ? null : (entry, candidate, signature, legacy?.Receipt, finding);
        }
        return null;
    }

    private static bool IsReviewedLegacyNote(string[] lines) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', lines)))) == ReviewedLegacyNoteHash;

    private static Match? ReadDiagnosis(GoalId goal, ProgressEvent entry)
    {
        if (entry.GoalId != goal || entry.TaskId is null || entry.Kind != ProgressKind.TaskNote ||
            !entry.Message.StartsWith(AttributedFailureDiagnosisRecord.Marker, StringComparison.Ordinal)) return null;
        var header = AttributedFailureDiagnosisCoordinator.MatchHeader(entry.Message.Split('\n', 2)[0].TrimEnd('\r'));
        return header.Success ? header : null;
    }

    private static HashSet<string>? ReadLegacyIdentities(Match route, string[] lines)
    {
        // Legacy solo routes allowed commas in a single identity. Their joined text is ambiguous.
        if (route.Groups["source"].Value == "solo" || lines.Length < 2 ||
            !lines[1].StartsWith(FailingTestsPrefix, StringComparison.Ordinal)) return null;
        var identities = Normalize(lines[1][FailingTestsPrefix.Length..].Split(','));
        return identities?.All(AttributedCohortFailureDetail.IsRepresentableTestName) == true ? identities : null;
    }

    private static HashSet<string>? ReadIdentities(string encoded)
    {
        try { return Normalize(JsonSerializer.Deserialize<string[]>(Convert.FromBase64String(encoded))); }
        catch (Exception ex) when (ex is FormatException or JsonException) { return null; }
    }

    private static HashSet<string>? Normalize(IReadOnlyList<string>? identities) =>
        identities is { Count: > 0 } && !identities.Any(string.IsNullOrWhiteSpace)
            ? identities.Select(test => test.Trim()).ToHashSet(StringComparer.Ordinal) : null;
}
