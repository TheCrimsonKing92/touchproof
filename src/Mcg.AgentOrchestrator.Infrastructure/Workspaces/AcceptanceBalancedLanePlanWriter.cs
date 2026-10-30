using Mcg.AgentOrchestrator.Core;
using AcceptanceManifestCheck = Mcg.AgentOrchestrator.Infrastructure.GoalAcceptanceVerifier.AcceptanceManifestCheck;

namespace Mcg.AgentOrchestrator.Infrastructure;

// Attempt-scoped observation owner. The layout never feeds back into this attempt's
// scheduling; only a successful completed attempt contributes duration observations.
internal sealed class AcceptanceBalancedLanePlanWriter
{
    private readonly object _sync = new();
    private readonly string _worktreePath;
    private readonly string? _attemptPrefix;
    private readonly GoalId? _goalId;
    private readonly Action<AcceptanceGateProgress> _emit;
    private readonly TimeProvider _clock;
    private readonly List<AcceptanceClassDurationHistoryStore.Sample> _samples = [];
    private readonly Dictionary<string, AcceptanceBalancedLaneCommand> _commands = new(StringComparer.Ordinal);
    private AcceptanceGateLanePlan _plan;

    private AcceptanceBalancedLanePlanWriter(AcceptanceGateLanePlan plan, string worktreePath,
        string? attemptPrefix, GoalId? goalId, Action<AcceptanceGateProgress> emit, TimeProvider clock)
    {
        _plan = plan;
        _worktreePath = worktreePath;
        _attemptPrefix = attemptPrefix;
        _goalId = goalId;
        _emit = emit;
        _clock = clock;
    }

    internal static AcceptanceBalancedLanePlanWriter Publish(AcceptanceGateLanePlan plan,
        string? attemptPrefix, GoalId? goalId, Action<AcceptanceGateProgress> emit, TimeProvider clock,
        string worktreePath, AcceptanceBalancedLaneLayout? layout)
    {
        var writer = new AcceptanceBalancedLanePlanWriter(plan with { BalancedLayout = layout }, worktreePath,
            attemptPrefix, goalId, emit, clock);
        AcceptanceGateLanePlanPublisher.Publish(writer._plan, attemptPrefix, goalId, emit, clock);
        return writer;
    }

    internal AcceptanceGateLanePlan Snapshot { get { lock (_sync) return _plan; } }

    internal void Record(AcceptanceManifestCheck check, AcceptanceCheckResult result)
    {
        if (_plan.BalancedLayout is not { } layout ||
            !AcceptancePolicyShardPlanner.ProjectMatches(check.Project, AcceptancePolicyShardPlanner.InfrastructureTestsProject) ||
            result.TestResultIsExplicitCrossAttemptReuse ||
            (result.TestResultAttemptId is { } attemptId && attemptId != _plan.AttemptId)) return;
        var laneName = _plan.Scheduled.FirstOrDefault(name => check.Name.EndsWith(": " + name, StringComparison.Ordinal)) ?? check.Name;
        var names = laneName == layout.ResidualLaneName ? AcceptanceClassDurationResultReader.ReadNames(result.TestResultPaths) : null;
        var durations = result.Passed ? AcceptanceClassDurationResultReader.Read(result.TestResultPaths) : null;
        lock (_sync)
        {
            if (names is not null)
                _plan = _plan with { BalancedLayout = _plan.BalancedLayout! with
                    { ResidualClasses = names, ResidualObserved = true } };
            if (durations is not null)
                _samples.AddRange(durations.Select(pair => new AcceptanceClassDurationHistoryStore.Sample(
                    _plan.AttemptId, laneName, pair.Key, pair.Value)));
        }
    }

    internal void RecordCommand(string? target, int characters, bool responseFile)
    {
        if (_plan.BalancedLayout is not { } layout || target is null) return;
        var lane = layout.Lanes.Select(item => item.Name).Append(layout.ResidualLaneName)
            .FirstOrDefault(name => target.EndsWith(": " + name, StringComparison.Ordinal));
        if (lane is null) return;
        lock (_sync)
            _commands[lane] = new(lane, characters, GoalAcceptanceVerifier.MaxCmdCommandLineLength, responseFile);
    }

    internal void AddResidualSummary(ICollection<AcceptanceCheckResult> checks)
    {
        if (Snapshot.BalancedLayout is not { ResidualObserved: true } layout) return;
        checks.Add(new("infrastructure residual coverage", true, 0, null, Advisory: true,
            ResultSummary: $"residual-count={layout.ResidualCount}; classes={string.Join(',', layout.ResidualClasses)}"));
    }

    internal void Complete(string outcome)
    {
        lock (_sync)
        {
            if (_plan.BalancedLayout is not { } layout) return;
            _plan = _plan with { BalancedLayout = layout with { Commands = _commands.Values.OrderBy(item => item.Lane, StringComparer.Ordinal).ToArray() } };
            AcceptanceGateLanePlanPublisher.Publish(_plan, _attemptPrefix, _goalId, _emit, _clock);
            AcceptanceClassDurationHistoryStore.RecordAttempt(_worktreePath, _plan.AttemptId, _samples,
                layout.HistoryWindow, outcome == "completed", outcome == "completed");
        }
    }
}
