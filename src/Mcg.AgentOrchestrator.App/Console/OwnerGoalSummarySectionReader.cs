using System.Text.RegularExpressions;
using System.Globalization;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.OwnerConsole;

internal sealed class OwnerGoalSummarySectionReader(
    Func<TaskVerificationRecord, CancellationToken, Task<string?>>? artifact,
    Func<GoalId, string, GoalPromotionFact?, CancellationToken, Task<OwnerGoalAcceptanceEvidence>>? acceptance)
{
    private readonly OwnerGoalVerifiedSectionReader _verified = new(artifact, acceptance);

    internal async Task<OwnerGoalSummarySection> PlannedAsync(Goal goal, CancellationToken token)
    {
        var task = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Planner)
            .OrderByDescending(task => task.LastDispatch?.DispatchedAt).FirstOrDefault();
        var reason = task is null || task.Status is WorkTaskStatus.Pending or WorkTaskStatus.Assigned or WorkTaskStatus.Running
            ? "planning pending" : "planning unavailable";
        var verification = task?.LastVerification;
        var dispatch = task?.LastDispatch;
        if (task?.Status == WorkTaskStatus.Completed && verification?.Succeeded == true && dispatch is not null)
        {
            if (dispatch.BriefVersion != goal.AuthoritativeBrief.Version || goal.RefinedSpec is not { } spec ||
                goal.AuthoritativeRefinedSpecVersion?.BriefVersion != goal.AuthoritativeBrief.Version ||
                !EffectiveAcceptanceCriteriaVersion.IsCapturedHashCurrent(spec, goal.EffectiveAcceptanceCriteriaCorrections,
                    verification.AcceptanceCriteriaVersionHash) ||
                verification.DispatchStartedAt != dispatch.DispatchedAt)
                reason = "planning stale — brief/criteria/dispatch association unavailable";
            else
            {
                try
                {
                    var text = await _verified.ReadArtifactAsync(verification, token);
                    if (text is not null && PlannerOutputContract.TryExtractDurablePlan(text, out var plan, out _) &&
                        PlannerOutputContract.SplitRequiredSections(plan).TryGetValue("acceptance criterion mapping", out var body))
                    {
                        var mapping = PlannerOutputContract.SelectCriterionMappings(body);
                        if (mapping.Count == spec.AcceptanceCriteria.Count &&
                            Enumerable.Range(1, spec.AcceptanceCriteria.Count).All(mapping.ContainsKey))
                        {
                            var bullets = mapping.OrderBy(pair => pair.Key).Select(pair =>
                                (Number: pair.Key, Plan: PlanField(pair.Value))).ToArray();
                            if (bullets.All(item => !string.IsNullOrWhiteSpace(item.Plan)))
                            {
                                var items = bullets.GroupBy(item => item.Plan, StringComparer.OrdinalIgnoreCase)
                                    .Select(group => group.Key! + " [criteria " + string.Join(",", group.Select(item => item.Number)) + "]").ToArray();
                                return new("completed plan", items.Take(5).Select(BoundItem).ToArray(),
                                    Source(verification, dispatch) + $"; brief v{dispatch.BriefVersion}; criteria {verification.AcceptanceCriteriaVersionHash}",
                                    Math.Max(0, items.Length - 5), items.Take(5).Where(item => item.Length > 360).ToArray(),
                                    SourceItems: items.Take(5).ToArray());
                            }
                        }
                        reason = "planning stale — criterion mapping incomplete or incompatible";
                    }
                    else reason = "planning unavailable — missing, oversized or unreadable durable artifact";
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { reason = "planning unavailable — " + ex.GetType().Name; }
            }
        }
        var expectations = Expectations(goal);
        return new(reason + "; Expected from brief", expectations.Take(5).Select(BoundItem).ToArray(),
            $"authoritative brief v{goal.AuthoritativeBrief.Version}" +
            (goal.AuthoritativeRefinedSpecVersion is { } version ? $"; spec v{version.Version}" : ""),
            Math.Max(0, expectations.Count - 5), expectations.Take(5).Where(item => item.Length > 360).ToArray(),
            SourceItems: expectations.Take(5).ToArray());
    }

    private static IReadOnlyList<string> Expectations(Goal goal)
    {
        return goal.RefinedSpec is { } current &&
            goal.AuthoritativeRefinedSpecVersion?.BriefVersion == goal.AuthoritativeBrief.Version
            ? EffectiveAcceptanceCriteriaVersion.BuildSnapshot(current, goal.EffectiveAcceptanceCriteriaCorrections)
            : goal.AuthoritativeBrief.Text.Replace("\r", "").Split('\n')
                .Where(line => !string.IsNullOrWhiteSpace(line))
                // Use the same generated-heading detection as the title, only on the brief's heading.
                .Select((line, index) => index == 0 && !line.TrimStart().StartsWith('#')
                    ? OwnerGoalTitle.From(goal.AuthoritativeBrief.Text) : line)
                .Where(line => !line.TrimStart().StartsWith('#')).Take(6).ToArray();
    }

    internal async Task<OwnerGoalSummarySection> ReportedAsync(Goal goal, string? candidate, CancellationToken token)
    {
        var task = goal.Tasks.Where(task => task.RequiredRole == AgentRole.Developer &&
                candidate is not null && string.Equals(task.LastDispatch?.ResultCommit, candidate, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(task => task.LastDispatch!.DispatchedAt).FirstOrDefault();
        if (task?.LastVerification is not { } record || task.LastDispatch is not { } dispatch ||
            record.DispatchStartedAt != dispatch.DispatchedAt)
            return new("unavailable — no candidate-bound structured implementation summary", [], "worker result");
        try
        {
            var text = await _verified.ReadArtifactAsync(record, token);
            if (text is not null && WorkerResultParser.TryParseFields(text, out var fields, out _) &&
                fields.TryGetValue("summary", out var summary) && !string.IsNullOrWhiteSpace(summary) &&
                fields.TryGetValue("commit", out var commit) &&
                (string.Equals(commit, candidate, StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(commit, "none", StringComparison.OrdinalIgnoreCase)))
                return new("worker-reported; not proof of behavior", [BoundItem(summary)], Source(record, dispatch) + "; candidate " + candidate,
                    ExpandedItems: summary.Length > 360 ? [summary] : null, SourceItems: [summary]);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        { return new("unavailable — " + ex.GetType().Name, [], Source(record, dispatch)); }
        return new("unavailable — structured summary absent or association unknown", [], Source(record, dispatch));
    }

    internal Task<(OwnerGoalSummarySection Section, OwnerGoalPreviewEvidence? Preview)> VerifiedAsync(Goal goal, string? candidate,
        GoalPromotionFact? landed, CancellationToken token) => _verified.VerifiedAsync(goal, candidate, landed, token);

    private static string Source(TaskVerificationRecord record, TaskDispatchRecord dispatch) =>
        (record.StandardOutputPath ?? "retained structured worker artifact") + $"; dispatch {dispatch.DispatchedAt:O}";

    private static string? PlanField(string mapping)
    {
        var match = Regex.Match(mapping, @"(?:^|;)\s*plan\s*=\s*(?<plan>.*?)(?=;\s*[\w_-]+\s*=|$)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["plan"].Value.Trim() : null;
    }

    internal static bool FullRevision(string? value) => value is { Length: 40 or 64 } && value.All(Uri.IsHexDigit);
    internal static string FileAreas(IReadOnlyList<string> files)
    {
        var areas = files.Take(100).Select(file =>
        {
            var path = file.Length > 2 ? file[2..].Replace('\\', '/') : file;
            var slash = path.LastIndexOf('/');
            return slash < 0 ? "repository root" : path[..slash];
        }).Distinct(StringComparer.Ordinal).ToArray();
        return string.Join(", ", areas.Take(5).Select(BoundItem)) + (areas.Length > 5 ? "; more areas omitted" : "");
    }
    internal static string BoundItem(string text)
    {
        var plain = text.Replace('\r', ' ').Replace('\n', ' ').Replace('{', ' ').Replace('}', ' ').Trim();
        if (plain.Length <= 360) return plain;
        var end = StringInfo.ParseCombiningCharacters(plain).TakeWhile(index => index <= 359).LastOrDefault();
        return plain[..end] + "… [full text in cited source / expanded text below]";
    }
}
