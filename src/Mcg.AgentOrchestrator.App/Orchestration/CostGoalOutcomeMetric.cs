using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

namespace Mcg.AgentOrchestrator.App.Orchestration;

internal sealed record OutcomeMetricGuard(string Name, string Rule);

internal sealed record OutcomeMetricLine(string Name, string Direction, string Target, int Window,
    OutcomeMetricGuard? Guard = null, AgentRole? Role = null);

internal sealed record OutcomeMetricSample(double? Value, int SampleSize);

internal sealed record OutcomeMetricBaseline(OutcomeMetricLine Line, double? Value, int SampleSize, string? Reason,
    OutcomeMetricSample? Guard = null);

internal sealed record CostGoalOutcomeFiling(DateTimeOffset FiledAt, string? SourceBacklogItemId,
    IReadOnlyList<OutcomeMetricBaseline> Baselines);

internal sealed record OutcomeGateObservation(string Id, DateTimeOffset RecordedAt, DateTimeOffset StartedAt,
    string? LaunchMainCommit, AcceptanceSelectedExecutionRecord Selection);

internal sealed class OutcomeGateWindow(OutcomeMetricLine line)
{
    private readonly string? primary = CostGoalOutcomeMetric.HasGateSource(line.Name) ? line.Name : null;
    private readonly string? guard = CostGoalOutcomeMetric.HasGateSource(line.Guard?.Name) ? line.Guard!.Name : null;
    private readonly List<double> values = [];
    private readonly List<double> guardValues = [];

    internal OutcomeMetricLine Line => line;
    internal bool HasSource => primary is not null || guard is not null;
    internal bool IsFull => (primary is null ? guardValues.Count : values.Count) == line.Window;

    internal void Add(OutcomeGateObservation gate)
    {
        if (IsFull) return;
        if (primary is not null)
        {
            if (CostGoalOutcomeMetric.Sample(primary, gate) is not { } value) return;
            values.Add(value);
        }
        if (guard is not null && CostGoalOutcomeMetric.Sample(guard, gate) is { } guardValue)
            guardValues.Add(guardValue);
    }

    internal (OutcomeMetricSample Metric, OutcomeMetricSample? Guard) Measure(int minimum)
    {
        // A live guard on a live metric must cover the exact same gates.
        var guardMinimum = primary is null ? minimum : Math.Max(minimum, values.Count);
        return (Measure(primary, values, minimum), guard is null ? null : Measure(guard, guardValues, guardMinimum));
    }

    private static OutcomeMetricSample Measure(string? metric, List<double> samples, int minimum) =>
        new(metric is not null && samples.Count >= minimum ? CostGoalOutcomeMetric.Aggregate(metric, samples) : null,
            samples.Count);
}

internal static class CostGoalOutcomeMetric
{
    internal const string CheckName = "cost-goal-outcome-metric";
    internal const string OverrideFlag = "--override-draft-checks";
    internal const string ReplaySelectedClasses = "replay-selected-classes";
    internal const string ReplayMissedFailures = "replay-missed-failures";
    internal const string WorkerTurns = "worker-turns-per-session";
    internal const string WorkerInput = "worker-input-volume-per-session";
    private const string TickStepPrefix = "tick-step-milliseconds:";
    internal static IReadOnlyList<string> MetricNames { get; } = Array.AsReadOnly(new[]
    {
        "selection-milliseconds", "executed-class-count", "gate-test-cpu-seconds",
        "full-gate-rate", "process-launches", ReplaySelectedClasses, ReplayMissedFailures,
        WorkerTurns, WorkerInput, TickStepPrefix + "<step>"
    });
    private const string LineFormat = "metric: <name>; direction: decrease|increase; target: <value or percent>; window: <N gates>" +
        "; optional guard: <metric>; must-not-increase|must-not-decrease. Tick-step metrics require window: <N ticks> and omit the guard. " +
        "Worker usage metrics accept window: <N sessions> and optional role: <worker role>";
    private static readonly Regex Heading = new(@"^##[ \t]+([^\r\n]+)", RegexOptions.CultureInvariant);
    private static readonly Regex Number = new(@"^[+-]?(?:[0-9]+(?:\.[0-9]+)?|\.[0-9]+)%?$", RegexOptions.CultureInvariant);
    private static readonly Regex Gates = new(@"^([0-9]+)(?:[ \t]+gates?)?$", RegexOptions.CultureInvariant);
    private static readonly Regex Ticks = new(@"^([0-9]+)[ \t]+ticks$", RegexOptions.CultureInvariant);
    private static readonly Regex Sessions = new(@"^([0-9]+)[ \t]+sessions$", RegexOptions.CultureInvariant);
    private static readonly Regex GateAttemptId = new(@"-(?<time>[0-9]{17})-[0-9a-f]{32}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

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
        var declarationCount = 0;
        var hasNoneDeclaration = false;
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
            declarationCount++;
            var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var malformed = false;
            string? guardRule = null;
            var parts = line.Split(';');
            for (var i = 0; i < parts.Length; i++)
            {
                var pair = parts[i].Split(':', 2);
                if (pair.Length != 2 || !fields.TryAdd(pair[0].Trim(), pair[1].Trim()))
                {
                    malformed = true;
                    continue;
                }
                if (pair[0].Trim().Equals("guard", StringComparison.OrdinalIgnoreCase) && i + 1 < parts.Length)
                    guardRule = parts[++i].Trim();
            }
            fields.TryGetValue("metric", out var name);
            if (name == "none")
            {
                hasNoneDeclaration = true;
                var hasReason = fields.TryGetValue("reason", out var reason);
                if (!hasReason) defects.Add("metric 'none' has no reason");
                else if (string.IsNullOrWhiteSpace(reason)) defects.Add("metric 'none' has a blank reason");
                if (parts.Length > (hasReason ? 2 : 1))
                    defects.Add("metric 'none' has unexpected fields. Only metric and reason are allowed");
                continue;
            }

            fields.TryGetValue("direction", out var direction);
            fields.TryGetValue("target", out var target);
            fields.TryGetValue("window", out var window);
            fields.TryGetValue("guard", out var guardName);
            fields.TryGetValue("role", out var roleName);
            var start = defects.Count;
            var tickStep = TickStepName(name);
            var workerMetric = IsWorkerUsageMetric(name);
            if (tickStep is not null)
            {
                if (!TickStepLedgerReader.StepNames.Contains(tickStep, StringComparer.Ordinal))
                    defects.Add($"unknown tick step '{tickStep}'. Allowed steps: {string.Join(", ", TickStepLedgerReader.StepNames)}");
            }
            else if (!MetricNames.Contains(name, StringComparer.Ordinal)) defects.Add($"unknown metric '{name}'");
            if (string.IsNullOrWhiteSpace(target)) defects.Add($"metric '{name}' has no target");
            else if (!Number.IsMatch(target) || !double.TryParse(target.TrimEnd('%'), CultureInfo.InvariantCulture, out var value) ||
                !double.IsFinite(value)) defects.Add($"metric '{name}' has an invalid numeric target '{target}'");
            if (direction is not ("decrease" or "increase")) defects.Add($"metric '{name}' has an invalid direction '{direction}'");
            var windowMatch = (tickStep is null ? Gates : Ticks).Match(window ?? "");
            if (workerMetric && !windowMatch.Success) windowMatch = Sessions.Match(window ?? "");
            var windowCount = 0;
            if (!windowMatch.Success || !int.TryParse(windowMatch.Groups[1].Value, out windowCount) || windowCount <= 0)
                defects.Add($"metric '{name}' has an invalid window '{window}'");
            if (guardName is not null)
            {
                if (tickStep is not null)
                    defects.Add($"tick-step metric '{name}' cannot carry a guard");
                else if (TickStepName(guardName) is not null)
                    defects.Add($"tick-step metric '{guardName}' cannot be a guard");
                else if (!MetricNames.Contains(guardName, StringComparer.Ordinal))
                    defects.Add($"metric '{name}' has an unknown guard metric '{guardName}'");
                if (guardRule is not ("must-not-increase" or "must-not-decrease"))
                    defects.Add($"metric '{name}' has an invalid guard rule '{guardRule}'");
            }
            AgentRole? role = null;
            if (workerMetric && roleName is not null)
            {
                if (Enum.TryParse<AgentRole>(roleName, ignoreCase: true, out var parsedRole) &&
                    roleName.Equals(Enum.GetName(parsedRole), StringComparison.OrdinalIgnoreCase))
                    role = parsedRole;
                else defects.Add($"metric '{name}' has an invalid role '{roleName}'");
            }
            if (malformed || fields.Count != 4 + (guardName is null ? 0 : 1) + (workerMetric && roleName is not null ? 1 : 0) ||
                fields.Keys.Any(key => !new[] { "metric", "direction", "target", "window", "guard", "role" }
                    .Contains(key, StringComparer.OrdinalIgnoreCase)))
                defects.Add($"metric '{name}' has malformed or unexpected fields");
            if (defects.Count == start)
                metrics.Add(new(name!, direction!, target!, windowCount, guardName is null ? null : new(guardName, guardRule!), role));
        }
        if (declarationCount == 0) defects.Add("no well-formed ## Outcome metric line");
        if (hasNoneDeclaration && declarationCount > 1) defects.Add("metric 'none' must be the only metric line");
        return (metrics, defects);
    }

    internal static AuthorBriefDraftCheck Check(string markdown, bool costBacklogItem)
    {
        if (!IsCostBrief(markdown, costBacklogItem)) return new(CheckName, true, "Not a cost brief.");
        return DescribeCheck(Parse(markdown));
    }

    private static AuthorBriefDraftCheck DescribeCheck(
        (IReadOnlyList<OutcomeMetricLine> Metrics, IReadOnlyList<string> Defects) parsed)
    {
        var (metrics, defects) = parsed;
        if (defects.Count > 0)
        {
            return new(CheckName, false, string.Join(" | ", defects) + $". Format: {LineFormat}. " +
                "When no listed metric measures what the goal changes, use `metric: none; reason: <text>` as the only metric line. " +
                $"Allowed: {string.Join(", ", MetricNames)}.");
        }
        return new(CheckName, true, metrics.Count == 0
            ? "Metric 'none' declared. No baseline or verdict is recorded."
            : $"{metrics.Count} valid outcome metric line(s).");
    }

    internal static CostGoalOutcomeFiling? CaptureAtFiling(string? source, string markdown,
        BacklogItem? item, OrchestratorWorkspace workspace, string? overrideReason)
    {
        var parsed = Parse(markdown);
        if (source is not null && IsCostBrief(markdown, item is not null && IsCostBacklogItem(item, workspace)))
        {
            var check = DescribeCheck(parsed);
            if (!check.Passed)
            {
                if (overrideReason is null)
                    throw new InvalidOperationException($"{check.Name} failed: {check.Detail} Use {OverrideFlag} <reason> to file anyway.");
                new ConductEventLogWriter(workspace.ConductEventsLogPath).Append("cost-goal-outcome-metric-override", null,
                    $"reason={overrideReason}; failedChecks={check.Name}; detail={check.Detail}; backlogItem={item?.Id ?? "none"}; source={source}");
            }
        }

        if (parsed.Metrics.Count == 0) return null;
        var filedAt = DateTimeOffset.UtcNow;
        var ticks = parsed.Metrics.Any(line => TickStepName(line.Name) is not null)
            ? TickStepLedgerReader.Read(workspace.ConductEventsLogPath) : [];
        var baselines = CaptureBaselines(parsed.Metrics, ReadRecordedGates(workspace.OrchestratorDirectory, filedAt),
            filedAt, ticks);
        return new(filedAt, item?.Id, baselines);
    }

    internal static IEnumerable<OutcomeGateObservation> ReadRecordedGates(
        string orchestratorDirectory, DateTimeOffset recordedBefore, CostGoalOutcomeGateMemo? memo = null)
    {
        var plans = DiscoverGatePlans(orchestratorDirectory, recordedBefore);
        if (memo is null)
            return plans.Select(ReadGate).OfType<OutcomeGateObservation>();

        var discovered = plans.ToArray();
        memo.Retain(discovered.Select(path => path.Prefix).ToHashSet(StringComparer.Ordinal));
        return discovered.Select(path => memo.GetOrRead(path.Prefix, path.ResultPath, path.MetadataPath,
            path.LanePlanPath, () => ReadGate(path))).OfType<OutcomeGateObservation>();
    }

    internal static IReadOnlyList<OutcomeMetricBaseline> BaselinesAt(IReadOnlyList<OutcomeMetricLine> lines,
        IEnumerable<OutcomeGateObservation> gates, DateTimeOffset filedAt, IEnumerable<TickStepObservation>? ticks = null) =>
        CaptureBaselines(lines, gates.Where(gate => gate.RecordedAt < filedAt)
            .OrderByDescending(gate => gate.RecordedAt).ThenBy(gate => gate.Id, StringComparer.Ordinal), filedAt, ticks);

    private static IReadOnlyList<OutcomeMetricBaseline> CaptureBaselines(IReadOnlyList<OutcomeMetricLine> lines,
        IEnumerable<OutcomeGateObservation> newestFirst, DateTimeOffset filedAt, IEnumerable<TickStepObservation>? ticks)
    {
        var windows = lines.Select(line => new OutcomeGateWindow(line)).ToArray();
        var supported = windows.Where(window => window.HasSource).ToArray();

        if (supported.Length > 0)
        {
            foreach (var gate in newestFirst.DistinctBy(gate => gate.Id, StringComparer.Ordinal))
            {
                foreach (var window in supported) window.Add(gate);
                if (supported.All(window => window.IsFull)) break;
            }
        }

        var newestTicks = (ticks ?? []).Where(tick => tick.Timestamp < filedAt)
            .OrderByDescending(tick => tick.Timestamp).ToArray();
        return windows.Select(window =>
        {
            var line = window.Line;
            if (TickStepName(line.Name) is { } step)
            {
                var sample = MeasureTickStep(line, step, newestTicks, Math.Min(line.Window, 3));
                return new OutcomeMetricBaseline(line, sample.Value, sample.SampleSize,
                    sample.Value is null ? "baseline-unavailable" : null);
            }
            var (metric, guard) = window.Measure(Math.Min(line.Window, 3));
            if (IsWorkerUsageMetric(line.Name))
                return new OutcomeMetricBaseline(line, null, 0, "awaiting-integration", guard);
            var reason = metric.Value is not null ? null : HasGateSource(line.Name) ? "baseline-unavailable" :
                IsReplayMetric(line.Name) ? "replay-reports-unavailable" : "no-gate-source";
            return new OutcomeMetricBaseline(line, metric.Value, metric.SampleSize, reason, guard);
        }).ToArray();
    }

    internal static string? TickStepName(string? metric) =>
        metric?.StartsWith(TickStepPrefix, StringComparison.Ordinal) == true ? metric[TickStepPrefix.Length..] : null;

    internal static OutcomeMetricSample MeasureTickStep(OutcomeMetricLine line, string step,
        IEnumerable<TickStepObservation> orderedTicks, int minimum)
    {
        var values = orderedTicks.Where(tick => tick.Step == step).Take(line.Window)
            .Select(tick => tick.Milliseconds).ToList();
        return new(values.Count >= minimum ? Aggregate(line.Name, values) : null, values.Count);
    }

    internal static bool HasGateSource(string? metric) =>
        metric is "selection-milliseconds" or "executed-class-count" or "full-gate-rate";

    internal static bool IsReplayMetric(string? metric) =>
        metric is ReplaySelectedClasses or ReplayMissedFailures;

    internal static bool IsWorkerUsageMetric(string? metric) => metric is WorkerTurns or WorkerInput;

    internal static double? Sample(string metric, OutcomeGateObservation gate)
    {
        var selection = gate.Selection;
        if (metric == "full-gate-rate") return selection.FullGateReason is null ? 0 : 1;
        if (selection.ExecutedClassCount is null or 0 && selection.FullGateReason is { } reason &&
            (reason.Contains("no changed source", StringComparison.OrdinalIgnoreCase) ||
             reason.Equals("Candidate changes are unavailable.", StringComparison.Ordinal))) return null;
        return metric switch
        {
            "selection-milliseconds" when selection.Mode != "Off" && double.IsFinite(selection.SelectionMilliseconds)
                => selection.SelectionMilliseconds,
            "executed-class-count" => selection.ExecutedClassCount,
            _ => null
        };
    }

    internal static double Aggregate(string metric, List<double> values)
    {
        if (metric == "full-gate-rate") return values.Average();
        values.Sort();
        var middle = values.Count / 2;
        return values.Count % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }

    private sealed record GatePlanPath(string Prefix, string AttemptId, DateTimeOffset RecordedAt)
    {
        internal string ResultPath => Prefix + ".result.json";
        internal string MetadataPath => Prefix + ".attempt.json";
        internal string LanePlanPath => Prefix + AcceptanceGateLanePlan.ArtifactSuffix;
    }

    private static IEnumerable<GatePlanPath> DiscoverGatePlans(string orchestratorDirectory, DateTimeOffset filedAt)
    {
        var root = Path.Combine(orchestratorDirectory, "acceptance-gate-attempts");
        return Entries(root, directories: true).SelectMany(directory => Entries(directory, directories: false))
            .Select(path =>
            {
                var prefix = path[..^AcceptanceGateLanePlan.ArtifactSuffix.Length];
                var attemptId = Path.GetFileName(prefix);
                var match = GateAttemptId.Match(attemptId);
                return match.Success && DateTimeOffset.TryParseExact(match.Groups["time"].Value, "yyyyMMddHHmmssfff",
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time) && time < filedAt
                    ? new GatePlanPath(prefix, attemptId, time) : null;
            }).OfType<GatePlanPath>().OrderByDescending(path => path.RecordedAt)
            .ThenBy(path => path.Prefix, StringComparer.Ordinal);
    }

    private static string[] Entries(string path, bool directories)
    {
        try
        {
            return directories ? Directory.GetDirectories(path) :
                Directory.GetFiles(path, "*" + AcceptanceGateLanePlan.ArtifactSuffix);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    // GateStatsReader uses this same timestamped results prefix. Both solo and train gate owners
    // publish the lane plan and terminal result there. This reads receipt paths without opening TRX.
    // The owner-written result carries its launch MainHeadSha. Parallel solo results can carry
    // rewritten heads, so their HolderCandidateKey is the only proof of the launch main.
    // This is repository provenance. The caller supplies adoption and build containment for verdicts.
    private static OutcomeGateObservation? ReadGate(GatePlanPath path)
    {
        var result = ReadEvidence<ConductorParallelAcceptanceRunArtifact>(path.ResultPath);
        if (result is not { Kind: "accepted", DispatchKind: "gate", Acceptance.TestResultPaths.Count: > 0 })
            return null;
        var metadataPath = path.MetadataPath;
        var metadata = ReadEvidence<ConductorParallelAcceptanceAttempt>(metadataPath);
        if (metadata is null && File.Exists(metadataPath)) return null;
        if (metadata is not null && (metadata.AttemptId != path.AttemptId || metadata.Kind != "gate" ||
            metadata.CompletedAt is null || metadata.Outcome is not
                (ConductorParallelAcceptanceAttemptOutcome.Passed or ConductorParallelAcceptanceAttemptOutcome.Failed)))
            return null;
        var plan = new OwnerConsoleGateLanePlanReader().Read(path.LanePlanPath, path.AttemptId);
        if (plan?.SelectedExecution is not { } selection) return null;
        var launchCommit = metadata?.HolderCandidateKey?.Split(':') switch
        {
            [var goalId, _, var main] when goalId == metadata?.GoalId && main != "unknown-main" => main,
            _ when metadata is null && path.AttemptId.Contains("-gate-", StringComparison.Ordinal) => result.MainHeadSha,
            _ => null
        };
        return new(path.Prefix, path.RecordedAt, metadata?.StartedAt ?? path.RecordedAt, launchCommit, selection);
    }

    private static T? ReadEvidence<T>(string path) where T : class
    {
        try { return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }
}
internal sealed class CostGoalOutcomeStore(string orchestratorDirectory)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    internal void RecordFiling(GoalId goalId, CostGoalOutcomeFiling filing) =>
        Write(goalId, new(filing, [], [], null, false), overwrite: false);

    internal CostGoalOutcomeRecord? Read(GoalId goalId)
    {
        var path = RecordPath(goalId);
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<CostGoalOutcomeRecord>(stream, Json) ??
                throw new InvalidDataException($"Unreadable outcome record for goal '{goalId}'.");
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal void WithGoalLock(GoalId goalId, Action action)
    {
        var path = Path.GetFullPath(RecordPath(goalId));
        if (OperatingSystem.IsWindows()) path = path.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)));
        using var mutex = new Mutex(false, $"Mcg.CostGoalOutcome.{hash}");
        try { mutex.WaitOne(); }
        catch (AbandonedMutexException) { /* The durable record remains the source on retry. */ }
        try { action(); }
        finally { mutex.ReleaseMutex(); }
    }

    internal void Write(GoalId goalId, CostGoalOutcomeRecord record, bool overwrite = true)
    {
        var path = RecordPath(goalId);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, record, Json);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { File.Delete(temporary); }
    }

    private string RecordPath(GoalId goalId) =>
        Path.Combine(orchestratorDirectory, "cost-goal-outcomes", $"{Guid.ParseExact(goalId.Value, "N"):N}.json");
}
