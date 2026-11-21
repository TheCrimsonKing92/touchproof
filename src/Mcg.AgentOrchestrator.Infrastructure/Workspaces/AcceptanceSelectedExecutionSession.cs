using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One attempt owns one computation. Identity calculation and Off execution never read selection inputs.
internal sealed class AcceptanceSelectedExecutionSession(
    AcceptanceSelectedExecutionMode mode, string worktreePath, IReadOnlyList<string>? changedFiles,
    bool attributionScoped = false,
    Func<string, IReadOnlyList<string>, AcceptanceSelectedExecutionInput>? read = null,
    AcceptanceGateEngineSettings? engineSettings = null,
    AcceptanceSelectedExecutionSessionSuspension? suspension = null,
    IReadOnlyList<string>? obligationSelectors = null,
    string? integrationBranch = null)
{
    private readonly AcceptanceSelectedExecutionMode _effectiveMode = mode == AcceptanceSelectedExecutionMode.Enforce && suspension is not null
        ? AcceptanceSelectedExecutionMode.Shadow : mode;
    private AcceptanceSelectedExecutionPlan? _plan;
    private IReadOnlyList<AcceptanceTestClassDescriptor> _classes = [];
    private IReadOnlyList<AcceptanceTestLane> _actual = [];
    private double _milliseconds;

    internal static async Task<AcceptanceSelectedExecutionSession> CreateAsync(
        AcceptanceGateEngineSettings settings, string worktreePath, IReadOnlyList<string>? changedFiles,
        bool attributionScoped, GoalAcceptanceVerifierTestOverrides testOverrides,
        string? projectHomeDirectory, IReadOnlyList<string>? obligationSelectors = null, string? integrationBranch = null)
    {
        var suspension = settings.SelectedExecutionMode == AcceptanceSelectedExecutionMode.Enforce
            ? await AcceptanceSelectedExecutionSessionSuspension.ReadAsync(worktreePath, projectHomeDirectory).ConfigureAwait(false)
            : null;
        return new(settings.SelectedExecutionMode, worktreePath, changedFiles, attributionScoped,
            testOverrides.ReadSelectedExecutionForTests, settings, suspension, obligationSelectors, integrationBranch);
    }

    internal bool Applied => _effectiveMode == AcceptanceSelectedExecutionMode.Enforce && _plan?.HasPlan == true;
    internal Func<IReadOnlyList<AcceptanceTestLane>, IReadOnlyList<AcceptanceTestLane>>? LaneTransform =>
        mode == AcceptanceSelectedExecutionMode.Off ? null : Transform;
    internal Func<IReadOnlyList<AcceptanceTestLane>, IReadOnlyList<AcceptanceWholeProjectCheck>, IReadOnlyList<AcceptanceTestLane>>? GateTransform =>
        mode == AcceptanceSelectedExecutionMode.Off ? null : Transform;
    internal Func<IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck>, IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck>>? ProjectCheckTransform =>
        mode == AcceptanceSelectedExecutionMode.Off ? null : TransformProjectChecks;
    internal IReadOnlyList<AcceptanceSelectedExecutionProjectCheckEntry> ProjectChecks => _plan?.ProjectChecks ?? [];

    internal IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> TransformProjectChecks(
        IReadOnlyList<GoalAcceptanceVerifier.AcceptanceManifestCheck> checks)
    {
        if (!Applied) return checks;
        return checks.SelectMany(check =>
        {
            var entry = ProjectChecks.FirstOrDefault(item => item.Matches(check));
            return entry?.Lane.Disposition == "skipped" ? Array.Empty<GoalAcceptanceVerifier.AcceptanceManifestCheck>() :
                new[] { entry?.Apply(check) ?? check };
        }).ToArray();
    }

    internal IReadOnlyList<AcceptanceTestLane> Transform(IReadOnlyList<AcceptanceTestLane> lanes) => Transform(lanes, []);

    internal IReadOnlyList<AcceptanceTestLane> Transform(IReadOnlyList<AcceptanceTestLane> lanes,
        IReadOnlyList<AcceptanceWholeProjectCheck> wholeProjectChecks)
    {
        if (mode == AcceptanceSelectedExecutionMode.Off) return lanes;
        _actual = lanes;
        var timer = Stopwatch.StartNew();
        try
        {
            if (attributionScoped || changedFiles is null || !changedFiles.Any())
                _plan = new(ReverseDependencySelectionOutcome.Unavailable,
                    attributionScoped ? "Attribution-scoped attempt." : "Candidate changes are unavailable.", lanes, [], [], [], []);
            else
            {
                var input = read is null ? AcceptanceImpactSelectionReader.ReadLive(worktreePath, changedFiles, engineSettings, integrationBranch)
                    : read(worktreePath, changedFiles);
                _classes = input.Classes;
                var targets = wholeProjectChecks.Where(check => check.SupportsSelectedExecution).ToArray();
                var inventories = new Dictionary<AcceptanceWholeProjectCheck, AcceptanceWholeProjectCheckInventory>();
                AcceptanceWholeProjectCheckInventory Inventory(AcceptanceWholeProjectCheck check)
                {
                    if (!inventories.TryGetValue(check, out var inventory))
                        inventories.Add(check, inventory = new(check.Name, check.Project, ReadProject(check.Project)));
                    return inventory;
                }
                IReadOnlyList<AcceptanceWholeProjectCheckInventory> Inventories() => wholeProjectChecks.Select(Inventory).ToArray();
                IReadOnlyList<AcceptanceWholeProjectCheckInventory> SelectableInventories() => targets.Select(Inventory).ToArray();
                var projectReachAvailable = true;
                var changedTestProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (read is null && input.Selection.Outcome == ReverseDependencySelectionOutcome.Resolved && targets.Length > 0)
                {
                    var routing = new ImpactReplayChangedSourceRouting(worktreePath);
                    var changedPaths = changedFiles.Select(path => Path.GetFullPath(path, worktreePath)).ToArray();
                    foreach (var check in targets)
                    {
                        var project = routing.TestProjects.FirstOrDefault(item => item.Path.Equals(
                            Path.GetFullPath(check.Project, worktreePath), StringComparison.OrdinalIgnoreCase));
                        if (project is null) continue;
                        var directory = Path.GetDirectoryName(project.Path)! + Path.DirectorySeparatorChar;
                        // Support selection currently reports non-infrastructure readers as files, not classes.
                        // Until those classes are available, changes to project files or linked sources require it whole.
                        if (changedPaths.Any(path => path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) ||
                            project.Sources.Contains(path, StringComparer.OrdinalIgnoreCase)))
                            changedTestProjects.Add(check.Project);
                    }
                    var productionSources = changedFiles.Where(path => path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) &&
                        !routing.IsTestSource(path)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (productionSources.Length > 0)
                    {
                        var reach = ReverseDependencyTestImpactReader.Read(worktreePath, productionSources,
                            propagateShippingSourceReach: true, additionalTargetTestProjects: targets.Select(check => check.Project)
                                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
                        projectReachAvailable = reach.Outcome == ReverseDependencySelectionOutcome.Resolved;
                        if (projectReachAvailable)
                        {
                            var projectClasses = SelectableInventories().SelectMany(check => check.Classes).ToArray();
                            var reached = reach.TestClassNames.Where(name => projectClasses.Any(item =>
                                name.Contains('.') || name.Contains('+') ? item.FullName == name : item.FullName.Split('.', '+').Last() == name));
                            input = input with { Selection = input.Selection with { TestClassNames = input.Selection.TestClassNames
                                .Concat(reached).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() } };
                        }
                    }
                }
                _plan = AcceptanceSelectedExecutionPlan.Create(changedFiles, input.Selection, lanes, input.Classes,
                    lanes.Where(lane => AcceptanceSelectedExecutionPlan.IsDefaultFloor(lane.Name))
                        .Select(lane => lane.Name).ToArray(), input.GuardClasses,
                    obligationSelectors ?? input.ObligationSelectors, Inventories, projectReachAvailable, SelectableInventories,
                    changedTestProjects, input.AssemblyScanReasons);
                IReadOnlyList<AcceptanceTestClassDescriptor> ReadProject(string project)
                {
                    try { return input.ProjectClasses?.Invoke(project) ?? []; }
                    catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException) { return []; }
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _plan = new(ReverseDependencySelectionOutcome.Unreadable,
                $"{ex.GetType().Name}: {ex.Message}", lanes, [], [], [], []);
            _classes = []; // An incomplete inventory cannot certify counts.
        }
        finally { _milliseconds = timer.Elapsed.TotalMilliseconds; }
        return _actual = Applied ? _plan!.ExecutionLanes : lanes;
    }

    internal bool ExcludesLane(string name) => Applied &&
        _plan!.Lanes.Any(lane => lane.Name == name && lane.Disposition == "skipped");

    internal AcceptanceGateLanePlan Observe(AcceptanceGateLanePlan observation)
    {
        if (mode == AcceptanceSelectedExecutionMode.Off)
            return observation with { SelectedExecution = AcceptanceSelectedExecutionRecord.Disabled };
        var shadow = _effectiveMode == AcceptanceSelectedExecutionMode.Shadow;
        var proposed = _plan?.Lanes ?? [];
        var kept = proposed.SelectMany(lane => lane.SelectedClasses).Distinct(StringComparer.Ordinal).ToArray();
        var original = proposed.SelectMany(lane => lane.SelectedClasses.Concat(lane.SkippedClasses))
            .Distinct(StringComparer.Ordinal).ToArray();
        var scheduled = _actual.Where(lane => observation.Scheduled.Contains(lane.Name, StringComparer.Ordinal)).ToArray();
        var executed = CountExecuted(scheduled);
        var skipped = original.Except(kept, StringComparer.Ordinal).Count();
        var record = new AcceptanceSelectedExecutionRecord(_effectiveMode.ToString(), _plan?.HasPlan == true ? "plan" : "full-gate",
            _plan?.Outcome.ToString(), _plan?.FullGateReason, _plan?.FloorLanes ?? [], _plan?.GuardClasses ?? [],
            _plan?.ObligationClasses ?? [],
            proposed.Concat(ProjectChecks.Select(entry => entry.Lane)).Select(lane => lane with
            {
                LaneSkipReason = lane.Disposition == "skipped" ? shadow
                    ? AcceptanceGateLaneSkip.ShadowLaneNotSelected : AcceptanceGateLaneSkip.LaneNotSelected : null,
                ClassSkipReason = lane.SkippedClasses.Count > 0 ? shadow
                    ? AcceptanceGateLaneSkip.ShadowClassNotSelected : AcceptanceGateLaneSkip.ClassNotSelected : null
            }).ToArray(), executed, Applied ? skipped : 0,
            _plan?.HasPlan == true ? kept.Length : null, _plan?.HasPlan == true ? skipped : null,
            _milliseconds, Applied, Applied,
            mode == AcceptanceSelectedExecutionMode.Enforce && suspension is not null ? "suspended-by-backstop-miss" : null,
            mode == AcceptanceSelectedExecutionMode.Enforce ? suspension?.MissId : null)
        {
            MethodFormResolutions = _plan?.MethodFormResolutions ?? [],
            UnresolvedObligationSelectors = _plan?.UnresolvedObligationSelectors ?? [],
            WholeProjectCoverage = _plan?.WholeProjectCoverage is { Count: > 0 } covered ? covered : null,
            AssemblyScanReasons = _plan?.AssemblyScanReasons is { Count: > 0 } reasons ? reasons : null
        };
        return observation with { SelectedExecution = record };
    }

    private int? CountExecuted(IReadOnlyList<AcceptanceTestLane> scheduled)
    {
        if (_classes.Count == 0) return null;
        try
        {
            var membership = scheduled.Select(AcceptanceLaneMembership.CreateClassMatcher).ToArray();
            return _classes.Where(item => membership.Any(includes => includes(item.FullName)))
                .Select(item => item.FullName).Distinct(StringComparer.Ordinal).Count();
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException)
        {
            // An unsupported baseline filter cannot turn observation into an execution failure.
            return null;
        }
    }
}
