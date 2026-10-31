using System.Globalization;

namespace Mcg.AgentOrchestrator.Core;

/// <summary>
/// Replays durable finding history. Candidate, main, receipt and feedback identities never define
/// a round or reset; only Developer rounds, criterion changes and operator rulings do.
/// </summary>
public static class FindingStagnationTracker
{
    public static IReadOnlyList<FindingStagnationDecision> Evaluate(
        Goal goal,
        IReadOnlyCollection<HumanInputRequest> humanInputRequests,
        TaskSpec triggeringTask,
        IReadOnlyList<ReviewFinding> routedFindings)
    {
        var decisions = new List<FindingStagnationDecision>();
        var developerResults = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer)
            .SelectMany(task => task.VerificationHistory).OrderBy(record => record.CompletedAt).ToArray();
        var latestDeveloper = developerResults.LastOrDefault();
        var routeSources = goal.Timeline.Select(FindingStagnationRouteSource.Read).OfType<FindingStagnationRouteSource>().ToArray();
        var reports = goal.Tasks.Where(task => task.RequiredRole is AgentRole.Reviewer or AgentRole.Tester)
            .SelectMany(task => task.VerificationHistory
                .Where(record => record.ReviewFindingContractViolation is null)
                .Select(record => (Task: task, Record: record)))
            .OrderBy(item => item.Record.CompletedAt).ToArray();

        foreach (var stableId in routedFindings.Where(finding => finding.State == ReviewFindingState.Open &&
                finding.Severity == FindingSeverity.Blocking)
            .Select(finding => finding.StableId).Distinct(StringComparer.Ordinal))
        {
            var resetAt = GetResetAt(goal, humanInputRequests, stableId);
            var observations = reports.Where(item => item.Record.CompletedAt > resetAt)
                .SelectMany(item => ReadRaisedFindings(item.Record)
                    .Where(finding => finding.StableId == stableId && finding.State == ReviewFindingState.Open &&
                        finding.Severity == FindingSeverity.Blocking)
                    .Select(finding => (item.Task, item.Record, Finding: finding,
                        Round: GetRoundKey(goal, item.Record.CompletedAt))))
                .GroupBy(item => item.Round)
                .Select(round =>
                {
                    var end = round.Max(item => item.Record.CompletedAt);
                    var routedRoles = routeSources.Where(source => source.StableId == stableId && source.RoundKey == round.Key && source.ResetAt == resetAt)
                        .Select(source => source.Role).ToArray();
                    // Source authority is stored separately from the feedback. Reviewer wins if
                    // both roles routed this finding in the same round.
                    var currentRound = round.Any(item => item.Task.Id == triggeringTask.Id &&
                        item.Record == triggeringTask.LastVerification);
                    var role = currentRound && triggeringTask.RequiredRole == AgentRole.Reviewer ? AgentRole.Reviewer
                        : routedRoles.Contains(AgentRole.Reviewer) ? AgentRole.Reviewer
                        : currentRound ? triggeringTask.RequiredRole
                        : routedRoles.Contains(AgentRole.Tester) ? AgentRole.Tester
                        : round.Any(item => item.Task.RequiredRole == AgentRole.Reviewer) ? AgentRole.Reviewer : AgentRole.Tester;
                    var authoritative = round.OrderBy(item => item.Task.RequiredRole == role ? 1 : 0)
                        .ThenBy(item => item.Record.CompletedAt).Last();
                    return new FindingStagnationObservation(round.Key, authoritative.Task.Id,
                        authoritative.Task.RequiredRole, authoritative.Finding.Description,
                        FindingDescriptionNormalizer.Normalize(authoritative.Finding.Description),
                        round.Select(item => item.Finding.Description)
                            .Where(text => text != authoritative.Finding.Description).Distinct(StringComparer.Ordinal).ToArray(),
                        round.Select(item => NormalizeFile(item.Finding.Location.File))
                            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                        end, authoritative.Record);
                }).OrderBy(item => item.RaisedAt).ToArray();
            if (observations.Length < 2) continue;
            var current = observations[^1];
            var previous = observations[^2];
            if (current.NormalizedDescription != previous.NormalizedDescription) continue;

            var matching = observations.Reverse().TakeWhile(item =>
                item.NormalizedDescription == current.NormalizedDescription).Reverse().ToArray();
            string? blocker = null;
            if (WorkerResultBlockers.TryFindBlocker(latestDeveloper, out var text) &&
                (HasToken(text, "exact-blocker") || HasToken(text, "premise-invalid")) &&
                text.Contains(stableId, StringComparison.Ordinal))
                blocker = text;

            var citedFiles = previous.CitedFiles.Concat(current.CitedFiles).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var proofs = reports.Where(item => item.Record.CompletedAt > previous.RaisedAt &&
                    item.Record.CompletedAt <= current.RaisedAt && item.Task.RequiredRole == current.Role)
                .Select(item => item.Record).ToArray();
            var proofAvailable = citedFiles.Length > 0 && citedFiles.All(IsResolvableFile) && proofs.Length > 0 &&
                !string.IsNullOrWhiteSpace(previous.Verification.ReviewedCommit) &&
                proofs.All(record => record.ReviewFindingTouchedAnchors is not null &&
                    string.IsNullOrWhiteSpace(record.ReviewFindingTouchProofDiagnostic) &&
                    !string.IsNullOrWhiteSpace(record.ReviewedCommit));
            var untouched = proofAvailable && !proofs.SelectMany(record => record.ReviewFindingTouchedAnchors!)
                .Any(anchor => citedFiles.Contains(NormalizeFile(anchor.File), StringComparer.OrdinalIgnoreCase));
            if (blocker is null && !untouched) continue;

            decisions.Add(new FindingStagnationDecision(goal.Id, stableId, triggeringTask.Id, current.Description,
                current.SecondaryDescriptions, blocker, matching.Select(item => item.RoundKey).ToArray(), untouched, resetAt));
        }
        return decisions;
    }

    public static string GetRoundKey(Goal goal, DateTimeOffset raisedAt) =>
        goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer).SelectMany(task => task.VerificationHistory)
            .Where(record => record.CompletedAt <= raisedAt).OrderBy(record => record.CompletedAt).LastOrDefault()
            ?.CompletedAt.UtcTicks.ToString(CultureInfo.InvariantCulture) ?? "initial";

    public static DateTimeOffset GetCriterionResetAt(Goal goal)
    {
        var resetAt = goal.EffectiveAcceptanceCriteriaCorrections
            .Where(item => item.SourceKind is ProgressKind.OperatorTaskNote or ProgressKind.GoalPolicyDecision)
            .Select(item => item.RecordedAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var versions = goal.RefinedSpecVersions.OrderBy(item => item.RecordedAt).ToArray();
        for (var index = 1; index < versions.Length; index++)
        {
            if (!versions[index - 1].Spec.AcceptanceCriteria.SequenceEqual(versions[index].Spec.AcceptanceCriteria, StringComparer.Ordinal) &&
                versions[index].RecordedAt > resetAt)
                resetAt = versions[index].RecordedAt;
        }
        return resetAt;
    }

    public static DateTimeOffset GetResetAt(Goal goal, IReadOnlyCollection<HumanInputRequest> requests, string stableId)
    {
        var prefix = FindingStagnationDecision.FingerprintPrefix(goal.Id, stableId);
        var rulingAt = requests.Where(request => request.GoalId == goal.Id && request.IsCompleted &&
                !request.WasDismissed && request.QuestionFingerprint.StartsWith(prefix, StringComparison.Ordinal) &&
                request.AuthoritativeAnswer?.Origin == HumanInputAnswerOrigin.Operator)
            .Select(request => request.AnsweredAt ?? DateTimeOffset.MinValue).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        var resolvedAt = goal.Timeline.Where(evt => evt.Kind == ProgressKind.HumanInputSuperseded &&
                evt.HumanInputSuperseded?.ResolvedStableIds.Contains(stableId, StringComparer.Ordinal) == true)
            .Select(evt => evt.OccurredAt).DefaultIfEmpty(DateTimeOffset.MinValue).Max();
        return new[] { GetCriterionResetAt(goal), rulingAt, resolvedAt }.Max();
    }

    private static IReadOnlyList<ReviewFinding> ReadRaisedFindings(TaskVerificationRecord record) =>
        WorkerResultBlockers.TryFindReviewFindingRound(record, out var round, out _) ? round.Findings : [];

    private static string NormalizeFile(string? file) => (file ?? string.Empty).Replace('\\', '/').Trim();

    private static bool IsResolvableFile(string file) => !string.IsNullOrWhiteSpace(file) &&
        !file.Contains('<') && !file.Contains('>') && !file.Equals("unknown", StringComparison.OrdinalIgnoreCase);

    private static bool HasToken(string text, string token) => text.StartsWith(token, StringComparison.OrdinalIgnoreCase) &&
        (text.Length == token.Length || !char.IsLetterOrDigit(text[token.Length]) && text[token.Length] != '-');
}
