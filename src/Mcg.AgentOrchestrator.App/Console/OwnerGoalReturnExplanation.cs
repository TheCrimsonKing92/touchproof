using System.Globalization;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal static class OwnerGoalReturnExplanation
{
    private sealed record ReturnFacts(DateTimeOffset ReturnedAt, DateTimeOffset CheckedAt, AgentRole Role,
        string? Candidate, string? Cause, string? Route, string? FindingId, string? Description,
        string? Feedback, IReadOnlyList<string> Evidence);

    private static ReturnFacts? Observe(Goal goal)
    {
        var developer = goal.Tasks.LastOrDefault(task =>
            task.RequiredRole == AgentRole.Developer && task.LastDispatch is not null);
        if (developer?.LastDispatch is not { } dispatch) return null;

        var previous = goal.Tasks.Where(task => task.RequiredRole is AgentRole.Tester or AgentRole.Reviewer)
            .SelectMany(task => task.VerificationHistory.Select(check => (task.RequiredRole, Check: check)))
            .Where(item => item.Check.CompletedAt < dispatch.DispatchedAt)
            .OrderByDescending(item => item.Check.CompletedAt).FirstOrDefault();
        if (previous.Check is not { } check) return null;

        var receipt = developer.RetryAdmissionHistory.Where(item => item.RecordedAt >= check.CompletedAt)
            .OrderByDescending(item => item.RecordedAt).FirstOrDefault();
        // The shared parser can fall back to a log; restrict it to retained goal state.
        var retainedCheck = check with { StandardOutputPath = null };
        var findings = (WorkerResultBlockers.TryFindReviewFindingRound(retainedCheck, out var round, out _)
                ? round.Findings : [])
            .Concat(check.MergedReviewFindings ?? [])
            .Where(finding => finding.State == ReviewFindingState.Open).ToArray();
        var finding = findings.FirstOrDefault(item =>
            receipt?.StableFindingIds?.Contains(item.StableId, StringComparer.Ordinal) == true)
            ?? findings.FirstOrDefault();

        return new(dispatch.DispatchedAt, check.CompletedAt, previous.RequiredRole, check.ReviewedCommit,
            receipt?.Cause.ToString(), receipt?.Route.ToString(), finding?.StableId, finding?.Description,
            developer.CriterionRetryFeedback.FirstOrDefault(), receipt?.EvidenceIdentities ?? []);
    }

    internal static IEnumerable<OwnerConsoleField> Fields(Goal goal)
    {
        if (Observe(goal) is not { } facts) yield break;
        yield return new("return/at", "Returned to Developer at", facts.ReturnedAt.ToString("O"));
        yield return new("return/check-at", "Previous check at", facts.CheckedAt.ToString("O"));
        yield return new("return/role", "Previous check role", facts.Role.ToString());
        yield return new("return/candidate", "Previous check candidate", facts.Candidate);
        yield return new("return/cause", "Return cause", facts.Cause);
        yield return new("return/route", "Return route", facts.Route);
        yield return new("return/finding", "Return finding ID", facts.FindingId);
        yield return new("return/description", "Return finding", facts.Description);
        yield return new("return/feedback", "Criterion feedback", facts.Feedback);
        for (var i = 0; i < facts.Evidence.Count; i++) yield return new("return/evidence/" + i, "Return evidence " + (i + 1), facts.Evidence[i]);
    }

    internal static IReadOnlyList<string> Lines(Goal goal, TimeZoneInfo zone)
    {
        if (Observe(goal) is not { } facts) return [];

        string Time(DateTimeOffset value) =>
            TimeZoneInfo.ConvertTime(value, zone).ToString("HH:mm:ss", CultureInfo.InvariantCulture);

        var candidate = string.IsNullOrWhiteSpace(facts.Candidate) ? ""
            : " on candidate " + facts.Candidate[..Math.Min(8, facts.Candidate.Length)];
        var lines = new List<string>
        {
            "Returned to Developer at " + Time(facts.ReturnedAt),
            $"  Previous check: {facts.Role} at {Time(facts.CheckedAt)}{candidate}"
        };
        var hasReason = false;
        if (facts.Cause is not null)
        {
            lines.Add($"  Cause: {facts.Cause}, route {facts.Route}");
            hasReason = true;
        }
        if (facts.FindingId is not null && FirstLine(facts.Description) is { } description)
        {
            lines.Add($"  Finding {facts.FindingId}: {description}");
            hasReason = true;
        }
        if (FirstLine(facts.Feedback) is { } feedback)
        {
            lines.Add("  Criterion feedback: " + feedback);
            hasReason = true;
        }
        if (facts.Evidence is { Count: > 0 } evidence)
            lines.Add("  Evidence: " + string.Join(", ", evidence.Take(3)));
        if (!hasReason) lines.Add("  Reason: not recorded in goal state");
        return lines;
    }

    private static string? FirstLine(string? text)
    {
        if (text is null) return null;
        var end = text.IndexOfAny(['\r', '\n']);
        var line = (end < 0 ? text : text[..end])
            .Replace('{', ' ').Replace('}', ' ').Replace('\r', ' ').Replace('\n', ' ');
        return string.IsNullOrWhiteSpace(line) ? null : line;
    }
}
