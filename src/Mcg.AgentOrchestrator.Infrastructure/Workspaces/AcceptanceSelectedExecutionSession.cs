using System.Diagnostics;
using Mcg.AgentOrchestrator.Core;

namespace Mcg.AgentOrchestrator.Infrastructure;

// One attempt owns one computation. Identity calculation and Off execution never read selection inputs.
internal sealed class AcceptanceSelectedExecutionSession(
    AcceptanceSelectedExecutionMode mode, string worktreePath, IReadOnlyList<string>? changedFiles,
    bool attributionScoped = false,
    Func<string, IReadOnlyList<string>, AcceptanceSelectedExecutionInput>? read = null,
    AcceptanceGateEngineSettings? engineSettings = null)
{
    private AcceptanceSelectedExecutionPlan? _plan;
    private IReadOnlyList<AcceptanceTestClassDescriptor> _classes = [];
    private IReadOnlyList<AcceptanceTestLane> _actual = [];
    private double _milliseconds;

    internal bool Applied => mode == AcceptanceSelectedExecutionMode.Enforce && _plan?.HasPlan == true;
    internal Func<IReadOnlyList<AcceptanceTestLane>, IReadOnlyList<AcceptanceTestLane>>? LaneTransform =>
        mode == AcceptanceSelectedExecutionMode.Off ? null : Transform;

    internal IReadOnlyList<AcceptanceTestLane> Transform(IReadOnlyList<AcceptanceTestLane> lanes)
    {
        if (mode == AcceptanceSelectedExecutionMode.Off) return lanes;
        _actual = lanes;
        var timer = Stopwatch.StartNew();
        try
        {
            if (attributionScoped || changedFiles is null || !changedFiles.Any())
                _plan = new(ReverseDependencySelectionOutcome.Unavailable,
                    attributionScoped ? "Attribution-scoped attempt." : "Candidate changes are unavailable.", lanes, [], [], []);
            else
            {
                var input = read is null ? AcceptanceImpactSelectionReader.ReadLive(worktreePath, changedFiles, engineSettings)
                    : read(worktreePath, changedFiles);
                _classes = input.Classes;
                _plan = AcceptanceSelectedExecutionPlan.Create(changedFiles, input.Selection, lanes, input.Classes,
                    lanes.Where(lane => AcceptanceSelectedExecutionPlan.IsDefaultFloor(lane.Name))
                        .Select(lane => lane.Name).ToArray(), input.GuardClasses);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _plan = new(ReverseDependencySelectionOutcome.Unreadable,
                $"{ex.GetType().Name}: {ex.Message}", lanes, [], [], []);
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
        var shadow = mode == AcceptanceSelectedExecutionMode.Shadow;
        var proposed = _plan?.Lanes ?? [];
        var kept = proposed.SelectMany(lane => lane.SelectedClasses).Distinct(StringComparer.Ordinal).ToArray();
        var original = proposed.SelectMany(lane => lane.SelectedClasses.Concat(lane.SkippedClasses))
            .Distinct(StringComparer.Ordinal).ToArray();
        var scheduled = _actual.Where(lane => observation.Scheduled.Contains(lane.Name, StringComparer.Ordinal)).ToArray();
        var executed = CountExecuted(scheduled);
        var skipped = original.Except(kept, StringComparer.Ordinal).Count();
        var record = new AcceptanceSelectedExecutionRecord(mode.ToString(), _plan?.HasPlan == true ? "plan" : "full-gate",
            _plan?.Outcome.ToString(), _plan?.FullGateReason, _plan?.FloorLanes ?? [], _plan?.GuardClasses ?? [],
            proposed.Select(lane => lane with
            {
                LaneSkipReason = lane.Disposition == "skipped" ? shadow
                    ? AcceptanceGateLaneSkip.ShadowLaneNotSelected : AcceptanceGateLaneSkip.LaneNotSelected : null,
                ClassSkipReason = lane.SkippedClasses.Count > 0 ? shadow
                    ? AcceptanceGateLaneSkip.ShadowClassNotSelected : AcceptanceGateLaneSkip.ClassNotSelected : null
            }).ToArray(), executed, Applied ? skipped : 0,
            _plan?.HasPlan == true ? kept.Length : null, _plan?.HasPlan == true ? skipped : null,
            _milliseconds, Applied);
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
