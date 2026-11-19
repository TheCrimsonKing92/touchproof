using Mcg.AgentOrchestrator.Core;
using Check = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;
using static Mcg.AgentOrchestrator.Infrastructure.AcceptancePolicyShardPlanner;

namespace Mcg.AgentOrchestrator.Infrastructure;

internal static class FocusedReverseDependentLaneSplitter
{
    private const string ClassSelectorPrefix = "FullyQualifiedName~";

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
            var prefixLength = RepositoryTestImpactPlanner.FocusedReverseDependentInfrastructureCheckName.Length + 2;
            var scheduled = Checks.Where(dispatched.Contains).Select(check => check.Name[prefixLength..]).ToArray();
            return plan with
            {
                Scheduled = plan.Scheduled.Concat(scheduled).Distinct(StringComparer.Ordinal).ToArray(),
                Skipped = plan.Skipped.Where(skip => !scheduled.Contains(skip.Name, StringComparer.Ordinal)).ToArray()
            };
        }
    }

    internal static Expansion? Resolve(
        IReadOnlyList<Check> required, IReadOnlyList<Check> manifest, PolicyShardPlan policy,
        AcceptanceBalancedLaneExpansion lanes, AcceptanceGateEngineSettings settings,
        Func<IClassDurationHistory>? readHistory, string worktreePath)
    {
        if (readHistory is null || policy.ForceFull || policy.IsAbsentProject(InfrastructureTestsProject) ||
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
                !check.Name.Equals($"{focused.Name}: {lane.Name}", StringComparison.OrdinalIgnoreCase)
                ? check.EstimatedSerialSeconds : double.PositiveInfinity));
        var resourceOwners = new Lazy<IReadOnlyList<(string Name, IReadOnlyList<string> Keys)>>(
            () => ResolveResourceOwners(lanes.Lanes, worktreePath));
        return Plan(focused, broad, pool, focusedBudget, laneBudget, readHistory(), occupied, name =>
            resourceOwners.Value.Where(owner => owner.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
                .SelectMany(owner => owner.Keys));
    }

    internal static Expansion Plan(
        Check focused, Check? broad, IReadOnlyList<AcceptanceTestLane> pool,
        double focusedBudget, double laneBudget, IClassDurationHistory history,
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

        var classes = tokens.Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(token => (Token: token, Name: token[ClassSelectorPrefix.Length..],
                Seconds: history.PredictSeconds("Infrastructure.Tests:" + token[ClassSelectorPrefix.Length..]) ?? 0))
            .ToArray();
        var knownTotal = classes.Where(item => IsUsable(item.Seconds)).Sum(item => item.Seconds);
        var unknown = classes.Where(item => !IsUsable(item.Seconds)).Select(item => item.Name).ToArray();
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
        var loads = new double[pool.Count];
        var searchLimitReached = false;
        if (!Place())
        {
            var detail = searchLimitReached
                ? "could not establish a valid assignment within the placement search limit"
                : "cannot be assigned within the available lane budgets";
            return FullGate(FormattableString.Invariant($"estimated {knownTotal} seconds {detail}"));
        }

        var shards = new List<Check>();
        for (var laneIndex = 0; laneIndex < pool.Count; laneIndex++)
        {
            var members = classes.Where((_, index) => assignments[index] == laneIndex).ToArray();
            if (members.Length == 0) continue;
            var lane = pool[laneIndex];
            var arguments = (string[])args.Clone();
            arguments[filterIndex + 1] = string.Join('|', members.Select(item => item.Token));
            var keys = focused.ExclusiveResourceKeys.Concat(lane.ExclusiveResourceKeys)
                .Concat(members.SelectMany(item => resourceKeysForClass?.Invoke(item.Name) ?? []))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            shards.Add(Copy(focused, $"{focused.Name}: {lane.Name}", arguments, loads[laneIndex], keys));
        }
        return new(shards);

        bool Place()
        {
            // Continue in first-fit order after a failed placement, with a bound on planning work.
            const int maximumBacktracks = 100_000;
            var nextLane = new int[order.Length];
            var previousLoads = new double[order.Length];
            var backtracks = 0;
            var position = 0;
            while (position >= 0 && position < order.Length)
            {
                var lane = nextLane[position]++;
                if (lane == pool.Count)
                {
                    if (++backtracks == maximumBacktracks)
                    {
                        searchLimitReached = true;
                        return false;
                    }
                    nextLane[position] = 0;
                    if (--position >= 0) loads[assignments[order[position]]] = previousLoads[position];
                    continue;
                }
                var index = order[position];
                var load = loads[lane] + classes[index].Seconds;
                var spare = remaining[lane] - loads[lane];
                if (load > remaining[lane] ||
                    Enumerable.Range(0, lane).Any(previous => remaining[previous] - loads[previous] == spare))
                    continue;
                assignments[index] = lane;
                previousLoads[position] = loads[lane];
                loads[lane] = load;
                position++;
            }
            return position == order.Length;
        }
    }

    private static bool IsClassSelector(string token) =>
        token.StartsWith(ClassSelectorPrefix, StringComparison.OrdinalIgnoreCase) &&
        token.Length > ClassSelectorPrefix.Length &&
        token[ClassSelectorPrefix.Length..].All(character => char.IsLetterOrDigit(character) || character is '_' or '.' or '+');

    private static bool IsUsable(double seconds) => double.IsFinite(seconds) && seconds > 0;

    private static IReadOnlyList<(string Name, IReadOnlyList<string> Keys)> ResolveResourceOwners(
        IReadOnlyList<AcceptanceTestLane> lanes, string worktreePath)
    {
        var owners = lanes.Where(lane => lane.ExclusiveResourceKeys.Count > 0)
            .Select(lane => (Includes: AcceptanceLaneMembership.CreateClassMatcher(lane), lane.ExclusiveResourceKeys))
            .ToArray();
        if (owners.Length == 0) return [];
        return AcceptanceTestClassSourceScanner.Scan(worktreePath).Select(item =>
            (item.FullName, (IReadOnlyList<string>)owners.Where(lane => lane.Includes(item.FullName))
                .SelectMany(lane => lane.ExclusiveResourceKeys).ToArray())).Where(item => item.Item2.Count > 0).ToArray();
    }

    private static Check Copy(
        Check check, string name, IReadOnlyList<string> arguments, double seconds, IReadOnlyList<string> resourceKeys) =>
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
            IsResolvedInfrastructureLane = check.IsResolvedInfrastructureLane,
            RequiresBuildSystemChange = check.RequiresBuildSystemChange,
            AllowsEmptySelection = check.AllowsEmptySelection,
            IsFocusedEvidenceSelection = check.IsFocusedEvidenceSelection,
            FocusedEvidenceTokens = check.FocusedEvidenceTokens,
            FocusedEvidenceSelections = check.FocusedEvidenceSelections
        };
}
