using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: each test owns its kernel; workspace, dispatch and reap use delegates.
public sealed class ConductorTerminalDependencyDropTests(ITestOutputHelper output)
    : ConductorBatchLoopTests(output)
{
    [Theory]
    [InlineData("Cancelled")]
    [InlineData("Superseded")]
    [InlineData("cancelled")]
    public void Run_TerminalStatusOnlyDependency_DropsEdgeBeforeAdvancing(string status)
    {
        var dependencyId = GoalId.New();
        var (kernel, dependent) = Dependent((dependencyId, status));
        Assert.DoesNotContain(kernel.Goals, goal => goal.Id == dependencyId);
        var created = new List<GoalId>();
        var reaped = new List<GoalId>();
        var detached = new List<GoalId>();
        var persisted = new List<GoalId>();

        // Bounded loop exit detaches eligible goals; observe it separately from dependency reaping.
        var summary = new ConductorBatchLoop(
            reapGoalRunningDispatches: (_, goal) => reaped.Add(goal.Id),
            detachGoalRunningDispatches: (_, goal) => detached.Add(goal.Id)).Run(
            kernel,
            MakeDriver(createWorkspace: goal =>
            {
                Assert.Empty(kernel.GetGoal(goal.Id).DependsOn);
                AssertRemovalEvent(kernel.GetGoal(goal.Id), dependencyId, status);
                created.Add(goal.Id);
                return "/tmp/workspace";
            }),
            ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1,
            persistGoalTick: (_, ids) => persisted.AddRange(ids));

        Assert.Empty(kernel.GetGoal(dependent.Id).DependsOn);
        AssertRemovalEvent(kernel.GetGoal(dependent.Id), dependencyId, status);
        Assert.Contains(dependent.Id, created);
        Assert.Contains(dependent.Id, persisted);
        Assert.DoesNotContain(dependent.Id, reaped);
        Assert.Equal(dependent.Id, Assert.Single(detached));
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Held);
        Assert.Equal(0, summary.Escalated);
        Assert.DoesNotContain(kernel.GetGoal(dependent.Id).Timeline,
            entry => entry.Message.Contains("dependency-terminal-without-landing", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Failed")]
    [InlineData("Retired")]
    public void Run_FailedOrRetiredDependency_PreservesEdgeAndEscalates(string status)
    {
        var dependencyId = GoalId.New();
        var (kernel, dependent) = Dependent((dependencyId, status));
        var created = new List<GoalId>();
        var dispatched = new List<GoalId>();
        var reaped = new List<GoalId>();

        var summary = new ConductorBatchLoop(reapGoalRunningDispatches: (_, goal) => reaped.Add(goal.Id)).Run(
            kernel,
            MakeDriver(createWorkspace: goal => { created.Add(goal.Id); return "/tmp/workspace"; },
                dispatchAndStart: goal =>
                {
                    dispatched.Add(goal.Id);
                    return DispatchStartOutcome.Started();
                }),
            ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);

        Assert.Equal(dependencyId, Assert.Single(kernel.GetGoal(dependent.Id).DependsOn));
        Assert.Empty(created);
        Assert.Empty(dispatched);
        Assert.Contains(dependent.Id, reaped);
        Assert.Equal(0, summary.Advanced);
        Assert.Equal(1, summary.Escalated);
        Assert.Contains(kernel.GetGoal(dependent.Id).Timeline, entry => entry.Message.Contains(
            $"dependency-terminal-without-landing: {dependencyId.Value[..8]} state={status}",
            StringComparison.Ordinal));
        AssertNoRemovalEvent(kernel.GetGoal(dependent.Id));
    }

    [Fact]
    public void Run_MultipleRemovableDependencies_DropsEachAndAdvances()
    {
        var cancelled = GoalId.New();
        var superseded = GoalId.New();
        var (kernel, dependent) = Dependent((cancelled, "Cancelled"), (superseded, "Superseded"));
        var created = new List<GoalId>();

        var summary = new ConductorBatchLoop().Run(kernel,
            MakeDriver(createWorkspace: goal => { created.Add(goal.Id); return "/tmp/workspace"; }),
            ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);

        Assert.Empty(kernel.GetGoal(dependent.Id).DependsOn);
        AssertRemovalEvent(kernel.GetGoal(dependent.Id), cancelled, "Cancelled");
        AssertRemovalEvent(kernel.GetGoal(dependent.Id), superseded, "Superseded");
        Assert.Contains(dependent.Id, created);
        Assert.Equal(1, summary.Advanced);
        Assert.Equal(0, summary.Escalated);
    }

    [Theory]
    [InlineData("Active", 1, 0)]
    [InlineData("Failed", 0, 1)]
    [InlineData("Retired", 0, 1)]
    public void Run_RemainingDependency_StillGovernsEligibility(string status, int held, int escalated)
    {
        var cancelled = GoalId.New();
        var remaining = GoalId.New();
        var (kernel, dependent) = Dependent((cancelled, "Cancelled"), (remaining, status));
        var created = new List<GoalId>();

        var summary = new ConductorBatchLoop().Run(kernel,
            MakeDriver(createWorkspace: goal => { created.Add(goal.Id); return "/tmp/workspace"; }),
            ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);

        Assert.Equal(remaining, Assert.Single(kernel.GetGoal(dependent.Id).DependsOn));
        AssertRemovalEvent(kernel.GetGoal(dependent.Id), cancelled, "Cancelled");
        Assert.Empty(created);
        Assert.Equal(0, summary.Advanced);
        Assert.Equal(held, summary.Held);
        Assert.Equal(escalated, summary.Escalated);
    }

    [Fact]
    public void Run_ActiveDependencyFirst_KeepsWaitingWithoutDroppingLaterEdge()
    {
        var active = GoalId.New();
        var cancelled = GoalId.New();
        var (kernel, dependent) = Dependent((active, "Active"), (cancelled, "Cancelled"));
        var created = new List<GoalId>();

        var summary = new ConductorBatchLoop().Run(kernel,
            MakeDriver(createWorkspace: goal => { created.Add(goal.Id); return "/tmp/workspace"; }),
            ConductorAutonomyPolicy.Conservative, NoStopPath(), maxIterations: 1);

        Assert.Equal(new[] { active, cancelled }, kernel.GetGoal(dependent.Id).DependsOn);
        AssertNoRemovalEvent(kernel.GetGoal(dependent.Id));
        Assert.Contains(kernel.GetGoal(dependent.Id).Timeline,
            entry => entry.Message.Contains($"waiting on dependency {active.Value[..8]}", StringComparison.Ordinal));
        Assert.Empty(created);
        Assert.Equal(1, summary.Held);
        Assert.Equal(0, summary.Escalated);
    }

    [Fact]
    public void Evaluate_StaleEdge_ReturnsHoldWithoutRemovalEvent()
    {
        var cancelled = GoalId.New();
        var (kernel, staleGoal) = Dependent((cancelled, "Cancelled"));
        var snapshot = kernel.ExportSnapshot();
        kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(row => row with { DependsOn = [] }).ToArray()
        });
        kernel.MarkKnownDependencyGoalStatuses([new KeyValuePair<GoalId, string>(cancelled, "Cancelled")]);
        var changed = new HashSet<GoalId>();
        Assert.Contains(cancelled, staleGoal.DependsOn);
        Assert.Empty(kernel.GetGoal(staleGoal.Id).DependsOn);

        var reason = ConductorDependencyEdgeReconciler.Evaluate(
            staleGoal, [], [], kernel, out var requiresPerson, new HashSet<string>(), changed);

        Assert.Equal($"dependency-terminal-without-landing: {cancelled.Value[..8]} state=Cancelled", reason);
        Assert.True(requiresPerson);
        Assert.Empty(changed);
        AssertNoRemovalEvent(kernel.GetGoal(staleGoal.Id));
    }

    [Fact]
    public void RemoveGoalDependency_UnknownDependency_RejectsWithoutMutation()
    {
        var unknown = GoalId.New();
        var (kernel, dependent) = Dependent((unknown, ""));
        Assert.False(kernel.TryGetKnownDependencyGoalStatus(unknown, out _));

        Assert.Throws<KeyNotFoundException>(() => kernel.RemoveGoalDependency(dependent.Id, unknown));

        Assert.Equal(unknown, Assert.Single(kernel.GetGoal(dependent.Id).DependsOn));
        AssertNoRemovalEvent(kernel.GetGoal(dependent.Id));
    }

    [Fact]
    public void RemoveGoalDependency_StatusOnlyEdgeAlreadyRemoved_RejectsRepeat()
    {
        var cancelled = GoalId.New();
        var (kernel, dependent) = Dependent((cancelled, "Cancelled"));

        kernel.RemoveGoalDependency(dependent.Id, cancelled);

        Assert.Empty(kernel.GetGoal(dependent.Id).DependsOn);
        Assert.Throws<InvalidOperationException>(() => kernel.RemoveGoalDependency(dependent.Id, cancelled));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Dependent) Dependent(
        params (GoalId Id, string Status)[] dependencies)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, DefaultAgents(), "dependent");
        var snapshot = kernel.ExportSnapshot();
        kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(row => row.Id == goal.Id.Value
                ? row with { DependsOn = dependencies.Select(dependency => dependency.Id.Value).ToArray() }
                : row).ToArray()
        });
        kernel.MarkKnownDependencyGoalStatuses(dependencies.Select(dependency =>
            new KeyValuePair<GoalId, string>(dependency.Id, dependency.Status)));
        return (kernel, kernel.GetGoal(goal.Id));
    }

    private static void AssertRemovalEvent(Goal dependent, GoalId dependencyId, string status)
    {
        var entry = Assert.Single(dependent.Timeline.Where(entry => entry.Message.StartsWith(
            $"DEPENDENCY_EDGE_DROPPED dependent={dependent.Id.Value} dependency={dependencyId.Value} ",
            StringComparison.Ordinal)));
        Assert.Equal(ProgressKind.GoalPolicyDecision, entry.Kind);
        Assert.Contains($"status={status} ", entry.Message);
    }

    private static void AssertNoRemovalEvent(Goal dependent) =>
        Assert.DoesNotContain(dependent.Timeline,
            entry => entry.Message.StartsWith("DEPENDENCY_EDGE_DROPPED ", StringComparison.Ordinal));
}
