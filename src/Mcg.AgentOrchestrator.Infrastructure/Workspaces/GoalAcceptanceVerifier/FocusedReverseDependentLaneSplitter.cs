using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedReverseDependentLaneSplitter
{
    private const string ClassSelectorPrefix = "FullyQualifiedName~";
    internal const string ShardNamePrefix = RepositoryTestImpactPlanner.FocusedReverseDependentInfrastructureCheckName + ": ";

    internal sealed record Expansion(IReadOnlyList<Check> Checks, string? FullGateReason = null)
    {
        internal IEnumerable<Check> Expand(Check check) =>
            (Checks.Count > 1 || FullGateReason is not null) && IsFocusedReverseDependentInfrastructureCheck(check)
                ? Checks : [check];

        internal AcceptanceGateLanePlan Observe(AcceptanceGateLanePlan plan, IReadOnlyList<Check> dispatched)
        {
            if (FullGateReason is not null)
            {
                return plan with
                {
                    SelectedExecution = (plan.SelectedExecution ?? AcceptanceSelectedExecutionRecord.Disabled) with
                    {
                        Decision = "full-gate",
                        Outcome = nameof(ReverseDependencySelectionOutcome.Abandoned),
                        FullGateReason = FullGateReason
                    }
                };
            }
            if (Checks.Count <= 1) return plan;
            var scheduled = Checks.Where(dispatched.Contains).Select(check => check.Name[ShardNamePrefix.Length..]).ToArray();
            return plan with
            {
                Scheduled = plan.Scheduled.Concat(scheduled).Distinct(StringComparer.Ordinal).ToArray(),
                Skipped = plan.Skipped.Where(skip => !scheduled.Contains(skip.Name, StringComparer.Ordinal)).ToArray()
            };
        }
    }

    internal static AcceptanceBalancedLanePlanWriter PublishLanePlan(
        Expansion? expansion, AcceptanceGateLanePlan plan, IReadOnlyList<Check> dispatched,
        string? attemptPrefix, GoalId? goalId, Action<AcceptanceGateProgress> emit, TimeProvider clock,
        string worktreePath, AcceptanceBalancedLaneLayout? layout) =>
        AcceptanceBalancedLanePlanWriter.Publish(expansion?.Observe(plan, dispatched) ?? plan,
            attemptPrefix, goalId, emit, clock, worktreePath, layout);

    internal static Expansion? Resolve(
        IReadOnlyList<Check> required, IReadOnlyList<Check> manifest, PolicyShardPlan policy,
        AcceptanceBalancedLaneExpansion lanes, AcceptanceGateEngineSettings settings,
        string worktreePath, SelectionSourceTexts? sourceTexts = null,
        IClassDurationHistory? history = null, IReadOnlyList<string>? inventory = null)
    {
        if (policy.ForceFull || policy.IsAbsentProject(InfrastructureTestsProject) ||
            required.SingleOrDefault(IsFocusedReverseDependentInfrastructureCheck) is not { } focused)
            return null;

        var broad = manifest.FirstOrDefault(IsBroadInfrastructureTestCheck) ??
                    required.FirstOrDefault(IsBroadInfrastructureTestCheck);
        var focusedBudget = settings.ResolveCheckTimeout(focused.TimeoutMinutes).TotalSeconds;
        var laneBudget = Math.Min(focusedBudget, settings.ResolveCheckTimeout(broad?.TimeoutMinutes).TotalSeconds);
        var poolNames = (lanes.Layout?.Lanes ?? []).Select(lane => lane.Name).ToHashSet(StringComparer.Ordinal);
        var pool = lanes.Lanes.Where(lane => poolNames.Contains(lane.Name)).ToArray();
        // The focused project check displaces the broad infrastructure check. Only separately
        // declared work for a destination lane consumes capacity alongside the focused selection.
        var otherWork = manifest.Concat(required).Where(check =>
            IsFocusedProjectDotnetCheck(check) && !IsFocusedReverseDependentInfrastructureCheck(check) &&
            ProjectMatches(check.Project!, InfrastructureTestsProject) && !IsSkippedPolicyShardCheck(check, policy))
            .DistinctBy(check => (check.Name, string.Join('\u001f', check.Arguments))).ToArray();
        var occupied = pool.ToDictionary(lane => lane.Name, lane => otherWork
            .Where(check => check.Name.EndsWith(": " + lane.Name, StringComparison.OrdinalIgnoreCase))
            .Sum(check => IsUsable(check.EstimatedSerialSeconds) &&
                !check.Name.Equals(ShardNamePrefix + lane.Name, StringComparison.OrdinalIgnoreCase)
                ? check.EstimatedSerialSeconds : double.PositiveInfinity));
        inventory ??= ReadProjectInventory(worktreePath, focused.Project!, sourceTexts)
            .Select(item => item.FullName).ToArray();
        var resourceOwners = new Lazy<(Func<string, bool> Includes, IReadOnlyList<string> Keys)[]>(() =>
            lanes.Lanes.Where(lane => lane.ExclusiveResourceKeys.Count > 0)
                .Select(lane => (AcceptanceLaneMembership.CreateClassMatcher(lane), lane.ExclusiveResourceKeys)).ToArray());
        return Plan(focused, broad, pool, focusedBudget, laneBudget,
            history ?? AcceptanceClassDurationHistoryAdapter.Read(worktreePath), inventory, occupied, name =>
            resourceOwners.Value.Where(owner => owner.Includes(name)).SelectMany(owner => owner.Keys));
    }

    internal static IReadOnlyList<AcceptanceTestClassDescriptor> ReadProjectInventory(
        string worktreePath, string project, SelectionSourceTexts? sourceTexts)
    {
        var projectPath = Path.GetFullPath(Path.Combine(worktreePath, project.Replace('\\', '/')));
        var projectDirectory = Path.GetDirectoryName(projectPath)!;
        XDocument document;
        try
        {
            document = XDocument.Parse(sourceTexts?.Read(projectPath) ?? SelectionFileReadProbe.ReadAllText(projectPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return [];
        }

        // Inventory uses the target project's SDK defaults and unconditional, project-local Compile items.
        // An indeterminate inventory leaves the selector unpriced and uses the existing full-gate path.
        if (document.Root?.Name.LocalName != "Project" ||
            document.Descendants().Any(item => item.Name.LocalName == "Import"))
            return [];

        var defaultItemProperties = document.Descendants().Where(item =>
            item.Name.LocalName is "EnableDefaultItems" or "EnableDefaultCompileItems").ToArray();
        if (defaultItemProperties.Any(item => IsConditional(item) ||
            !bool.TryParse(item.Value, out _)))
            return [];
        var defaultItems = document.Root.Attribute("Sdk") is not null && !defaultItemProperties
            .GroupBy(item => item.Name.LocalName).Any(group => !bool.Parse(group.Last().Value));

        var rules = new List<(Regex Pattern, bool Include)>();
        foreach (var item in document.Descendants().Where(item => item.Name.LocalName == "Compile"))
        {
            var include = (string?)item.Attribute("Include");
            var remove = (string?)item.Attribute("Remove");
            if (include is null && remove is null) continue;
            if (item.Parent is not { } group || group.Parent != document.Root || group.Name.LocalName != "ItemGroup" ||
                IsConditional(item) || item.Attribute("Exclude") is not null)
                return [];

            foreach (var entry in (include ?? remove!).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pattern = entry.Replace('\\', '/');
                while (pattern.StartsWith("./", StringComparison.Ordinal)) pattern = pattern[2..];
                if (pattern.StartsWith('/') || pattern.Contains(':') || pattern.Split('/').Contains("..") ||
                    pattern.Contains("$(", StringComparison.Ordinal) || pattern.Contains("@(", StringComparison.Ordinal) || pattern.Contains('%'))
                    return [];
                var expression = Regex.Escape(pattern)
                    .Replace(@"\*\*/", "(?:.*/)?", StringComparison.Ordinal)
                    .Replace(@"\*\*", ".*", StringComparison.Ordinal)
                    .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
                    .Replace(@"\?", "[^/]", StringComparison.Ordinal);
                rules.Add((new Regex($@"\A{expression}\z",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking),
                    include is not null));
            }
        }

        var files = FocusedEvidenceRequestResolver.EnumerateFocusedEvidenceSourceFiles(worktreePath, project)
            .Where(file =>
            {
                var relative = Path.GetRelativePath(projectDirectory, file).Replace('\\', '/');
                var rule = rules.LastOrDefault(rule => rule.Pattern.IsMatch(relative));
                return rule.Pattern is not null ? rule.Include :
                    defaultItems && !relative.Split('/').Any(part => part == "artifacts" || part.StartsWith('.'));
            }).ToArray();
        return AcceptanceTestClassSourceScanner.ScanWithLaneConstants(worktreePath, files, sourceTexts);
    }

    private static bool IsConditional(XElement item) => item.AncestorsAndSelf().Any(parent =>
        parent.Name.LocalName is "Choose" or "When" or "Otherwise" || parent.Attribute("Condition") is not null);

    internal static Expansion Plan(
        Check focused, Check? broad, IReadOnlyList<AcceptanceTestLane> pool,
        double focusedBudget, double laneBudget, IClassDurationHistory history, IReadOnlyList<string> inventory,
        IReadOnlyDictionary<string, double>? occupiedSeconds = null,
        Func<string, IEnumerable<string>>? resourceKeysForClass = null)
    {
        var args = focused.Arguments.ToArray();
        static bool IsFilterFlag(string arg) => arg.Equals("--filter", StringComparison.OrdinalIgnoreCase);
        var filterIndex = Array.FindIndex(args, IsFilterFlag);
        var filter = filterIndex >= 0 && filterIndex + 1 < args.Length ? args[filterIndex + 1] : string.Empty;
        var tokens = filter.Split('|');
        var remaining = pool.Select(lane =>
            Math.Max(0, laneBudget - (occupiedSeconds?.GetValueOrDefault(lane.Name) ?? 0))).ToArray();
        var poolBudget = remaining.Sum();
        Expansion FullGate(string detail) => new(
            [broad ?? Copy(focused, "infrastructure tests",
                args.Where((arg, index) => !IsFilterFlag(arg) && (index == 0 || !IsFilterFlag(args[index - 1]))).ToArray(),
                0, focused.ExclusiveResourceKeys)],
            FormattableString.Invariant(
                $"Focused reverse-dependent infrastructure overflow: {detail}; focused lane budget {focusedBudget} seconds, destination lane budget {laneBudget} seconds, available pool budget {poolBudget} seconds."));

        // The planner produces a flat union of class contains-selectors. Other filter
        // grammars cannot be priced here. A contains-selector can also match a longer class name.
        if (tokens.Any(token => !IsClassSelector(token)) ||
            args.Count(IsFilterFlag) != 1)
            return FullGate("total estimate unknown (known subtotal 0 seconds); unsupported class selection filter");

        var selectors = tokens.Select(token => token[ClassSelectorPrefix.Length..])
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var matchedSelectors = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var selectedClasses = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in inventory)
        foreach (var selector in selectors)
        {
            if (!name.Contains(selector, StringComparison.OrdinalIgnoreCase)) continue;
            matchedSelectors.Add(selector);
            selectedClasses.Add(name);
        }
        var classes = selectedClasses.Order(StringComparer.Ordinal)
            .Select(name => (Name: name, Seconds: history.PredictSeconds("Infrastructure.Tests:" + name) ?? 0)).ToArray();
        var knownTotal = classes.Where(item => IsUsable(item.Seconds)).Sum(item => item.Seconds);
        var unknown = classes.Where(item => !IsUsable(item.Seconds)).Select(item => item.Name)
            .Concat(selectors.Where(selector => !matchedSelectors.Contains(selector))).ToArray();
        if (unknown.Length > 0)
            return FullGate(FormattableString.Invariant(
                $"total estimate unknown (known subtotal {knownTotal} seconds); no usable estimate for {string.Join(", ", unknown)}"));
        if (knownTotal <= focusedBudget)
            return new([focused]);
        if (pool.Count < 2 || knownTotal > poolBudget)
            return FullGate(FormattableString.Invariant(
                $"estimated {knownTotal} seconds exceeds the focused lane budget and the available pool cannot split it"));

        var order = Enumerable.Range(0, classes.Length)
            .OrderByDescending(index => classes[index].Seconds).ThenBy(index => classes[index].Name, StringComparer.Ordinal)
            .ToArray();
        var largest = classes[order[0]];
        if (largest.Seconds > remaining.Max())
            return FullGate(FormattableString.Invariant(
                $"estimated {knownTotal} seconds; {largest.Name} estimates {largest.Seconds} seconds and exceeds every available lane"));

        var assignments = new int[classes.Length];
        if (!PlaceClasses(order.Select(index => classes[index].Seconds).ToArray(), remaining, assignments))
            return FullGate(FormattableString.Invariant(
                $"estimated {knownTotal} seconds cannot be assigned within the available lane budgets"));

        var shards = new List<Check>();
        for (var laneIndex = 0; laneIndex < pool.Count; laneIndex++)
        {
            var members = order.Where((_, position) => assignments[position] == laneIndex)
                .Select(index => classes[index]).OrderBy(item => item.Name, StringComparer.Ordinal).ToArray();
            if (members.Length == 0) continue;
            var lane = pool[laneIndex];
            var arguments = (string[])args.Clone();
            arguments[filterIndex + 1] = string.Join('|', members.Select(item => "FullyQualifiedName=" + item.Name));
            var keys = focused.ExclusiveResourceKeys.Concat(lane.ExclusiveResourceKeys)
                .Concat(members.SelectMany(item => resourceKeysForClass?.Invoke(item.Name) ?? []))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            shards.Add(Copy(focused, ShardNamePrefix + lane.Name, arguments, members.Sum(item => item.Seconds), keys,
                isResolvedLane: true));
        }
        return new(shards);

    }

    private static bool PlaceClasses(double[] seconds, double[] remaining, int[] assignments)
    {
        // Lanes with the same spare capacity are interchangeable for feasibility. A position can
        // only be revisited after its earlier search was exhausted, even if lane order differed.
        var visited = new HashSet<(int Position, string Capacities)>();
        var nextLane = new int[seconds.Length];
        var previousCapacity = new double[seconds.Length];
        var position = 0;
        while (position >= 0 && position < seconds.Length)
        {
            if (nextLane[position] == 0 && !visited.Add((position, string.Join(',',
                remaining.Order().Select(value => value.ToString("R", CultureInfo.InvariantCulture))))))
                nextLane[position] = remaining.Length;

            var lane = nextLane[position]++;
            if (lane == remaining.Length)
            {
                nextLane[position] = 0;
                if (--position >= 0) remaining[assignments[position]] = previousCapacity[position];
                continue;
            }

            var capacity = remaining[lane];
            if (seconds[position] > capacity || Array.IndexOf(remaining, capacity) != lane) continue;
            assignments[position] = lane;
            previousCapacity[position] = capacity;
            remaining[lane] = capacity - seconds[position];
            position++;
        }
        return position == seconds.Length;
    }

    private static bool IsClassSelector(string token) =>
        token.StartsWith(ClassSelectorPrefix, StringComparison.OrdinalIgnoreCase) &&
        token.Length > ClassSelectorPrefix.Length &&
        token[ClassSelectorPrefix.Length..].All(character => char.IsLetterOrDigit(character) || character is '_' or '.' or '+');

    private static bool IsUsable(double seconds) => double.IsFinite(seconds) && seconds > 0;

    private static Check Copy(
        Check check, string name, IReadOnlyList<string> arguments, double seconds, IReadOnlyList<string> resourceKeys,
        bool isResolvedLane = false) =>
        new()
        {
            Name = name,
            Type = check.Type,
            Command = check.Command,
            Project = check.Project,
            Arguments = arguments,
            Reports = check.Reports,
            Pattern = check.Pattern,
            FilePath = check.FilePath,
            TimeoutMinutes = check.TimeoutMinutes,
            Advisory = check.Advisory,
            Runner = check.Runner,
            EstimatedSerialSeconds = seconds,
            ExclusiveResourceKeys = resourceKeys,
            IsResolvedInfrastructureLane = isResolvedLane || check.IsResolvedInfrastructureLane,
            RequiresBuildSystemChange = check.RequiresBuildSystemChange,
            AllowsEmptySelection = check.AllowsEmptySelection,
            IsFocusedEvidenceSelection = check.IsFocusedEvidenceSelection,
            FocusedEvidenceTokens = check.FocusedEvidenceTokens,
            FocusedEvidenceSelections = check.FocusedEvidenceSelections
        };
}
