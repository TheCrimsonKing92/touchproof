using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// A complete, translated candidate plan, or a full-gate decision. Never a partial selection.
internal sealed record AcceptanceSelectedExecutionPlan(
    ReverseDependencySelectionOutcome Outcome, string? FullGateReason,
    IReadOnlyList<AcceptanceTestLane> ExecutionLanes,
    IReadOnlyList<AcceptanceSelectedExecutionLane> Lanes,
    IReadOnlyList<string> FloorLanes, IReadOnlyList<string> GuardClasses, IReadOnlyList<string> ObligationClasses)
{
    public IReadOnlyList<AcceptanceSelectedExecutionMethodFormResolution> MethodFormResolutions { get; init; } = [];
    public IReadOnlyList<AcceptanceSelectedExecutionCheckCoverage> WholeProjectCoverage { get; init; } = [];
    public IReadOnlyList<AcceptanceSelectedExecutionProjectCheckEntry> ProjectChecks { get; init; } = [];

    internal bool HasPlan => Outcome == ReverseDependencySelectionOutcome.Resolved;

    internal static AcceptanceSelectedExecutionPlan Create(IReadOnlyList<string>? changedFiles,
        ReverseDependencyTestSelection selection, IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<AcceptanceTestClassDescriptor> classes, IReadOnlyList<string> floorLanes,
        IReadOnlyList<string> guardClasses, IReadOnlyList<string>? obligationSelectors = null,
        Func<IReadOnlyList<AcceptanceWholeProjectCheckInventory>>? wholeProjectChecks = null,
        bool projectReachAvailable = true,
        Func<IReadOnlyList<AcceptanceWholeProjectCheckInventory>>? selectableProjectChecks = null,
        IReadOnlySet<string>? changedTestProjects = null)
    {
        var obligations = new HashSet<string>(StringComparer.Ordinal);
        var methodFormResolutions = new List<AcceptanceSelectedExecutionMethodFormResolution>();
        var coverage = new List<AcceptanceSelectedExecutionCheckCoverage>();
        IReadOnlyList<AcceptanceWholeProjectCheckInventory>? projectInventories = null;
        AcceptanceSelectedExecutionPlan Full(ReverseDependencySelectionOutcome outcome, string? reason) =>
            new(outcome, reason ?? outcome.ToString(), lanes, [], floorLanes, guardClasses,
                obligations.Order(StringComparer.Ordinal).ToArray())
            { MethodFormResolutions = methodFormResolutions.ToArray(), WholeProjectCoverage = coverage.ToArray() };
        if (changedFiles is null || !changedFiles.Any())
            return Full(ReverseDependencySelectionOutcome.Unavailable, "Candidate changes are unavailable.");
        if (selection.Outcome != ReverseDependencySelectionOutcome.Resolved)
            return Full(selection.Outcome, selection.Reason);
        if (!classes.Any())
            return Full(ReverseDependencySelectionOutcome.Unavailable, "Candidate test-class inventory is unavailable.");

        var membership = lanes.ToDictionary(lane => lane.Name, AcceptanceLaneMembership.CreateClassMatcher, StringComparer.Ordinal);
        AcceptanceTestClassDescriptor[] Resolve(string selector, IEnumerable<AcceptanceTestClassDescriptor>? inventory = null) =>
            (inventory ?? classes).Where(item => selector.Contains('.') || selector.Contains('+')
            ? item.FullName == selector : item.FullName.Split('.', '+').Last() == selector).ToArray();
        var laneClasses = classes.Select(item => item.FullName).ToHashSet(StringComparer.Ordinal);
        bool InLane(AcceptanceTestClassDescriptor item) => laneClasses.Contains(item.FullName) &&
            membership.Values.Any(includes => includes(item.FullName));
        string? unresolved = null;
        foreach (var selector in obligationSelectors ?? [])
        {
            // Validate shape before matching: filters and paths must never certify a partial gate.
            var wellFormed = Regex.IsMatch(selector,
                @"\A[_\p{L}\p{Nl}][_\p{L}\p{Nl}\p{Nd}\p{Pc}\p{Mn}\p{Mc}\p{Cf}]*(?:[.+][_\p{L}\p{Nl}][_\p{L}\p{Nl}\p{Nd}\p{Pc}\p{Mn}\p{Mc}\p{Cf}]*)*\z",
                RegexOptions.CultureInvariant);
            var matches = wellFormed ? Resolve(selector) : [];
            var methodForm = false;
            var separator = selector.LastIndexOf('.');
            // A final '.' introduces a method; '+' only separates nested types.
            if (wellFormed && matches.Length == 0 && separator > selector.LastIndexOf('+'))
            {
                matches = Resolve(selector[..separator]);
                methodForm = true;
            }
            if (wellFormed && wholeProjectChecks is not null && (matches.Length == 0 || matches.Any(item => !InLane(item))))
            {
                projectInventories ??= wholeProjectChecks();
                var inventory = classes.Concat(projectInventories.SelectMany(check => check.Classes)).ToArray();
                matches = Resolve(selector, inventory);
                methodForm = false;
                if (matches.Length == 0 && separator > selector.LastIndexOf('+'))
                {
                    matches = Resolve(selector[..separator], inventory);
                    methodForm = true;
                }
            }
            if (matches.Length == 0 || matches.Any(item => !InLane(item) &&
                !(projectInventories ?? []).Any(check => check.Classes.Any(candidate => candidate.FullName == item.FullName))))
                unresolved ??= selector;
            else
            {
                obligations.UnionWith(matches.Select(item => item.FullName));
                foreach (var check in projectInventories ?? [])
                    foreach (var name in matches.Where(item => !InLane(item) &&
                        check.Classes.Any(candidate => candidate.FullName == item.FullName))
                        .Select(item => item.FullName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    {
                        var covered = new AcceptanceSelectedExecutionCheckCoverage(selector, name, check.CheckName);
                        if (!coverage.Contains(covered)) coverage.Add(covered);
                    }
                if (methodForm)
                    foreach (var name in matches.Select(item => item.FullName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
                    {
                        var resolution = new AcceptanceSelectedExecutionMethodFormResolution(selector, name);
                        if (!methodFormResolutions.Contains(resolution)) methodFormResolutions.Add(resolution);
                    }
            }
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        var guards = new HashSet<string>(StringComparer.Ordinal);
        // Only selectable projects need inventories for reach; other project scans remain obligation-driven.
        var selectableProjects = (selectableProjectChecks?.Invoke() ?? projectInventories ?? []).Where(check =>
            new AcceptanceWholeProjectCheck(check.CheckName, check.Project).SupportsSelectedExecution).ToArray();
        var selectableClasses = classes.Concat(selectableProjects.SelectMany(check => check.Classes)).ToArray();
        foreach (var selector in selection.TestClassNames.Concat(guardClasses).Distinct(StringComparer.Ordinal))
        {
            var matches = Resolve(selector, selectableClasses);
            if (matches.Length == 0 || matches.Any(item => !InLane(item) &&
                !selectableProjects.Any(check => check.Classes.Any(candidate => candidate.FullName == item.FullName))))
                return Full(ReverseDependencySelectionOutcome.Abandoned, "UnmappedTestClass: " + selector);
            names.UnionWith(matches.Select(item => item.FullName));
            if (guardClasses.Contains(selector, StringComparer.Ordinal))
                guards.UnionWith(matches.Select(item => item.FullName));
        }
        if (unresolved is not null)
            return Full(ReverseDependencySelectionOutcome.Abandoned, "UnresolvedObligationSelector: " + unresolved);
        names.UnionWith(obligations);

        var execution = new List<AcceptanceTestLane>();
        var records = new List<AcceptanceSelectedExecutionLane>();
        foreach (var lane in lanes)
        {
            var translated = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(lane.Filter).ToArray();
            var eligible = classes.Where(item => membership[lane.Name](item.FullName))
                .Select(item => item.FullName).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var whole = floorLanes.Contains(lane.Name, StringComparer.Ordinal) || !translated.Contains("--filter-class");
            var retained = whole ? eligible : eligible.Where(names.Contains).ToArray();
            var skipped = eligible.Except(retained, StringComparer.Ordinal).ToArray();
            var disposition = whole ? "retained" : retained.Length == 0 ? "skipped" : "narrowed";
            string? filter = null;
            if (whole) execution.Add(lane);
            else if (retained.Length > 0)
            {
                // Membership has already intersected the original inclusion and class exclusions.
                // Preserve every exclusion, including traits, in the executable filter as well.
                var exclusions = Regex.Split(lane.Filter, @"[&|]").Select(token => token.Trim().Trim('(', ')').Trim())
                    .Where(token => token.Contains("!=", StringComparison.Ordinal) || token.Contains("!~", StringComparison.Ordinal));
                filter = "(" + string.Join('|', retained.Select(name => "FullyQualifiedName=" + name)) + ")";
                foreach (var exclusion in exclusions) filter += "&" + exclusion;
                _ = AcceptanceCheckCommandBuilder.TranslateResolvedLaneFilter(filter).ToArray();
                execution.Add(lane with { Filter = filter, AllowsEmptySelection = true });
            }
            records.Add(new(lane.Name, disposition, retained, skipped, filter ?? (whole ? lane.Filter : null)));
        }
        return new(ReverseDependencySelectionOutcome.Resolved, null, execution, records, floorLanes,
            guards.Order(StringComparer.Ordinal).ToArray(), obligations.Order(StringComparer.Ordinal).ToArray())
        {
            MethodFormResolutions = methodFormResolutions.ToArray(), WholeProjectCoverage = coverage.ToArray(),
            ProjectChecks = selectableProjects.Select(check => AcceptanceSelectedExecutionProjectCheckEntry.Create(
                check, names, floorLanes, projectReachAvailable,
                hasChangedTestFiles: changedTestProjects?.Contains(check.Project) == true)).ToArray()
        };
    }

    internal static bool IsDefaultFloor(string name) =>
        name.StartsWith("Process spawning", StringComparison.Ordinal) ||
        name.StartsWith("Isolated process spawning", StringComparison.Ordinal) ||
        name is "Chaos gate" or "Remainder merge train and ratchet";
}
