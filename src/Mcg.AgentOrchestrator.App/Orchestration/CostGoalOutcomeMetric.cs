using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OutcomeMetricLine(string Name, string Direction, string Target, int Window);

internal static class CostGoalOutcomeMetric
{
    internal const string CheckName = "cost-goal-outcome-metric";
    internal const string OverrideFlag = "--override-draft-checks";
    internal static IReadOnlyList<string> MetricNames { get; } = Array.AsReadOnly(new[]
    {
        "selection-milliseconds", "executed-class-count", "gate-test-cpu-seconds",
        "full-gate-rate", "process-launches"
    });
    private const string LineFormat = "metric: <name>; direction: decrease|increase; target: <value or percent>; window: <N gates>";
    private static readonly Regex Heading = new(@"^##[ \t]+([^\r\n]+)", RegexOptions.CultureInvariant);
    private static readonly Regex Number = new(@"^[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)%?$", RegexOptions.CultureInvariant);
    private static readonly Regex Gates = new(@"^([0-9]+)(?:[ \t]+gates?)?$", RegexOptions.CultureInvariant);

    internal static bool IsCostBacklogItem(BacklogItem item, string? membershipEpicId) =>
        BoardFillReadyItemSelector.HasTag(item, "cost") ||
        membershipEpicId?.StartsWith("79801150", StringComparison.OrdinalIgnoreCase) == true;

    internal static bool IsCostBacklogItem(BacklogItem item, OrchestratorWorkspace workspace)
    {
        var membership = File.Exists(workspace.PortfolioStorePath)
            ? PortfolioStore.OpenReadOnly(workspace.PortfolioStorePath).GetBacklogMembershipAsync(item.Id).GetAwaiter().GetResult()
            : null;
        return IsCostBacklogItem(item, membership?.EpicId);
    }

    internal static bool IsCostBrief(string markdown, bool costBacklogItem) => costBacklogItem ||
        markdown.ReplaceLineEndings("\n").Split('\n').Any(line => line.StartsWith("Expected effect", StringComparison.Ordinal));

    internal static (IReadOnlyList<OutcomeMetricLine> Metrics, IReadOnlyList<string> Defects) Parse(string markdown)
    {
        var metrics = new List<OutcomeMetricLine>();
        var defects = new List<string>();
        var inSection = false;
        var foundLine = false;
        foreach (var (text, _) in AuthorBriefDraftChecks.UnfencedLines(markdown))
        {
            var heading = Heading.Match(text);
            if (heading.Success)
            {
                inSection = heading.Groups[1].Value.Trim().Equals("Outcome metric", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inSection) continue;
            var line = text.Trim();
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
                line = line[2..].Trim();
            line = line.Trim('`');
            if (!Regex.IsMatch(line, @"\bmetric\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) continue;
            foundLine = true;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var malformed = false;
            foreach (var part in line.Split(';'))
            {
                var pair = part.Split(':', 2);
                if (pair.Length != 2 || !fields.TryAdd(pair[0].Trim(), pair[1].Trim())) malformed = true;
            }
            fields.TryGetValue("metric", out var name);
            fields.TryGetValue("direction", out var direction);
            fields.TryGetValue("target", out var target);
            fields.TryGetValue("window", out var window);
            var start = defects.Count;
            if (!MetricNames.Contains(name, StringComparer.Ordinal)) defects.Add($"unknown metric '{name}'");
            if (string.IsNullOrWhiteSpace(target)) defects.Add($"metric '{name}' has no target");
            else if (!Number.IsMatch(target)) defects.Add($"metric '{name}' has an invalid numeric target '{target}'");
            if (direction is not ("decrease" or "increase")) defects.Add($"metric '{name}' has an invalid direction '{direction}'");
            var gateMatch = Gates.Match(window ?? "");
            var gateCount = 0;
            if (!gateMatch.Success || !int.TryParse(gateMatch.Groups[1].Value, out gateCount) || gateCount <= 0)
                defects.Add($"metric '{name}' has an invalid window '{window}'");
            if (malformed || fields.Count != 4 || fields.Keys.Any(key =>
                !new[] { "metric", "direction", "target", "window" }.Contains(key, StringComparer.OrdinalIgnoreCase)))
                defects.Add($"metric '{name}' has malformed or unexpected fields");
            if (defects.Count == start) metrics.Add(new(name!, direction!, target!, gateCount));
        }
        if (!foundLine) defects.Add("no well-formed ## Outcome metric line");
        return (metrics, defects);
    }

    internal static AuthorBriefDraftCheck Check(string markdown, bool costBacklogItem)
    {
        if (!IsCostBrief(markdown, costBacklogItem)) return new(CheckName, true, "Not a cost brief.");
        var (metrics, defects) = Parse(markdown);
        return new(CheckName, defects.Count == 0, defects.Count == 0
            ? $"{metrics.Count} valid outcome metric line(s)."
            : string.Join(" | ", defects) + $". Format: {LineFormat}. Allowed: {string.Join(", ", MetricNames)}.");
    }

    internal static void EnforceAtFiling(string? source, string markdown,
        BacklogItem? item, OrchestratorWorkspace workspace, string? overrideReason)
    {
        if (source is null) return;
        var check = Check(markdown, item is not null && IsCostBacklogItem(item, workspace));
        if (check.Passed) return;
        if (overrideReason is null)
            throw new InvalidOperationException($"{check.Name} failed: {check.Detail} Use {OverrideFlag} <reason> to file anyway.");
        new ConductEventLogWriter(workspace.ConductEventsLogPath).Append("cost-goal-outcome-metric-override", null,
            $"reason={overrideReason}; failedChecks={check.Name}; detail={check.Detail}; backlogItem={item?.Id ?? "none"}; source={source}");
    }
}
