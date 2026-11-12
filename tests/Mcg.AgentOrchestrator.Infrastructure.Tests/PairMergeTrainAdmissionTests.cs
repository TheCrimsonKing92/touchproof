using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: all admission state is in memory and the live census override is scoped to this async context.
public sealed class PairMergeTrainAdmissionTests : ConductorBatchLoopTests
{
    public PairMergeTrainAdmissionTests(ITestOutputHelper output) : base(output) { }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    public void CompatibleGoals_AdmitOldestFirstTrainsWithoutCohorts(int count)
    {
        using var scenario = new Scenario(count);

        var summary = scenario.Tick();

        Assert.Equal(count == 5 ? new[] { 3, 2 } : new[] { count },
            scenario.Trains.Select(members => members.Count));
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.Trains.SelectMany(members => members));
        Assert.Equal(0, scenario.CohortCalls);
        Assert.Empty(scenario.SoloGoals);
        Assert.Equal(count, summary.Advanced);
        Assert.Equal(0, summary.Held);
        Assert.Equal(0, summary.Escalated);
    }

    [Fact]
    public void GreenPair_ReturnsBothTrainLandingResults()
    {
        using var scenario = new Scenario(2);

        var summary = scenario.Tick();

        Assert.Equal(scenario.Goals.Select(goal => goal.Id), Assert.Single(scenario.Trains));
        Assert.Equal(2, summary.Advanced);
        Assert.All(scenario.Goals, goal => Assert.Contains(scenario.TickResult!.ProgressLines!,
            line => line.StartsWith("GOAL ", StringComparison.Ordinal) &&
                line.Contains(goal.Id.Value[..8], StringComparison.Ordinal) &&
                line.Contains("result=executed state=Verified", StringComparison.Ordinal)));
        Assert.Contains(scenario.TickResult!.ProgressLines!, line =>
            line.StartsWith("ACCEPTANCE_TRAIN ", StringComparison.Ordinal) &&
            line.Contains("outcome=passed landings=2", StringComparison.Ordinal));
        Assert.Equal(0, scenario.CohortCalls);
        Assert.Empty(scenario.SoloGoals);
    }

    [Fact]
    public void RedPair_OrdinaryFallbackDoesNotRegroupInTheSameTick()
    {
        using var scenario = new Scenario(2, fallback: true);

        scenario.Tick();

        Assert.Equal(scenario.Goals.Select(goal => goal.Id), Assert.Single(scenario.Trains));
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.SoloGoals);
        Assert.Equal(0, scenario.CohortCalls);
    }

    [Fact]
    public void TrainsDisabled_PairUsesOrdinaryAdmissionWithoutCohorts()
    {
        using var scenario = new Scenario(2, trainsEnabled: false);

        scenario.Tick();

        Assert.Empty(scenario.Trains);
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.SoloGoals);
        Assert.Equal(0, scenario.CohortCalls);
    }

    [Fact]
    public void UnrefinedPair_UsesOrdinaryAdmissionWithoutCohorts()
    {
        using var scenario = new Scenario(2, refined: false);

        scenario.Tick();

        Assert.Empty(scenario.Trains);
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.SoloGoals);
        Assert.Equal(0, scenario.CohortCalls);
    }

    [Fact]
    public void OverlappingPair_UsesOrdinaryAdmissionWithoutCohorts()
    {
        using var scenario = new Scenario(2, overlapping: true);

        scenario.Tick();

        Assert.Empty(scenario.Trains);
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.SoloGoals);
        Assert.Equal(0, scenario.CohortCalls);
    }

    private sealed class Scenario : IDisposable
    {
        internal AgentOrchestratorKernel Kernel { get; } = new();
        internal Goal[] Goals { get; }
        internal List<IReadOnlyList<GoalId>> Trains { get; } = [];
        internal List<GoalId> SoloGoals { get; } = [];
        internal int CohortCalls { get; private set; }
        internal BatchTickSummary? TickResult { get; private set; }
        private readonly ConductorDriver _driver;
        private readonly IDisposable _census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);

        internal Scenario(int count, bool fallback = false, bool trainsEnabled = true, bool overlapping = false,
            bool refined = true)
        {
            Goals = Enumerable.Range(0, count).Select(index =>
            {
                var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(Kernel, DefaultAgents(),
                    $"Update tests/Mcg.AgentOrchestrator.Core.Tests/PairMember{index}.cs");
                PassVerificationAt(Kernel, goal, goal.Tasks.Single(), DateTimeOffset.UnixEpoch.AddSeconds(index));
                return goal;
            }).ToArray();
            if (refined) MergeTrainAdmissionTestContext.Refine(Kernel, Goals);
            var paths = Goals.Select((goal, index) => (goal.Id, Path: (IReadOnlyList<string>)
                [$"tests/Mcg.AgentOrchestrator.Core.Tests/PairMember{(overlapping ? 0 : index)}.cs"]))
                .ToDictionary(item => item.Id, item => item.Path);
            var projector = new GateReadyCandidateProjector(
                id => new GateReadyCandidateRevisionPair(id.Value.PadRight(40, 'b')[..40], new string('a', 40)),
                id => new GateReadyLandingScopeObservation(true, paths[id]),
                (_, _, _) => new GateReadyMergeTreeObservation(true));
            _driver = MakeDriver(
                getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
                runAcceptanceWithSlot: (goal, _) =>
                {
                    SoloGoals.Add(goal.Id);
                    return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria;
                },
                classifyRisk: _ => ChangeRiskTier.DocsOnly,
                getLandingFileScopes: goal => paths[goal.Id],
                isVerificationGateSatisfied: _ => true,
                gateReadyCandidateProjector: projector,
                mergeTrainsEnabled: trainsEnabled,
                runAcceptanceCohort: (_, _, _) =>
                {
                    CohortCalls++;
                    throw new InvalidOperationException("New grouped admission must not create a cohort.");
                },
                runMergeTrain: (selection, _, policy) =>
                {
                    Trains.Add(selection.Members.Select(member => member.GoalId).ToArray());
                    return new ConductorMergeTrainRunResult(null,
                        fallback ? new Dictionary<string, ConductorAdvanceResult>() : selection.Members.ToDictionary(
                            member => member.GoalId.Value,
                            member => new ConductorAdvanceResult(member.GoalId.Value, member.GoalId.Value[..8], policy.Name,
                                new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified,
                                    "Landed by merge train receipt fake-passed-pair.")), StringComparer.Ordinal),
                        [], fallback ? "outcome=failed fallback=ordinary" : $"outcome=passed landings={selection.Members.Count}");
                });
        }

        internal BatchLoopSummary Tick() => new ConductorBatchLoop().Run(
            Kernel, _driver, ConductorAutonomyPolicy.Permissive, NoStopPath(), maxIterations: 1,
            onTick: tick => TickResult = tick);

        public void Dispose() => _census.Dispose();
    }
}
