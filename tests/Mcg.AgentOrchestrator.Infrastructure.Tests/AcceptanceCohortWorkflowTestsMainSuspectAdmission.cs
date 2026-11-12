using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// The circuit scenario owns its repository and stores; admission uses fake gate runners.
public sealed class AcceptanceCohortWorkflowTestsMainSuspectAdmission : ConductorBatchLoopTests
{
    public AcceptanceCohortWorkflowTestsMainSuspectAdmission(ITestOutputHelper output) : base(output) { }

    // Git fixture setup and two bounded ticks have no elapsed-time acceptance contract.
    [Fact]
    [Trait("Category", "CrossTick")]
    public async Task MainSuspect_HoldsAdmissionUntilOperatorClear()
    {
        using var scenario = AcceptanceCohortWorkflowTestsMainSuspect.CreateScenario();
        scenario.Run();
        Assert.Equal(AcceptanceEngineHealth.Unhealthy, (await scenario.Circuit.ReadAsync()).Health);
        var verifierRuns = scenario.RunCount;
        MergeTrainAdmissionTestContext.Refine(scenario.Kernel, scenario.Goals);
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var members = scenario.Selection.Members.ToDictionary(member => member.GoalId);
        var trains = new List<IReadOnlyList<GoalId>>();
        var driver = MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            classifyRisk: _ => ChangeRiskTier.DocsOnly,
            getLandingFileScopes: goal => members[goal.Id].LandingPaths,
            isVerificationGateSatisfied: _ => true,
            gateReadyCandidateProjector: new GateReadyCandidateProjector(
                id => new GateReadyCandidateRevisionPair(members[id].BranchRevision, members[id].MainRevision),
                id => new GateReadyLandingScopeObservation(true, members[id].LandingPaths),
                (_, _, _) => new GateReadyMergeTreeObservation(true)),
            runAcceptanceWithSlot: (_, _) => throw new InvalidOperationException("The compatible pair must enter a train."),
            runAcceptanceCohort: (_, _, _) => throw new InvalidOperationException("New cohort admission is prohibited."),
            runMergeTrain: (selection, _, policy) =>
            {
                trains.Add(selection.Members.Select(member => member.GoalId).ToArray());
                return new ConductorMergeTrainRunResult(null, selection.Members.ToDictionary(
                    member => member.GoalId.Value,
                    member => new ConductorAdvanceResult(member.GoalId.Value, member.GoalId.Value[..8], policy.Name,
                        new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "Controlled train gate pending.")),
                    StringComparer.Ordinal), [], "outcome=pending");
            });
        var attempts = driver.ParallelAcceptanceAttemptCoordinator;
        Assert.Empty(attempts.GetUnreconciledAttempts(scenario.Goals.Select(goal => goal.Id.Value).ToArray()));
        var loop = new ConductorBatchLoop(acceptanceEngineCircuit: scenario.Circuit,
            conductEventLogWriter: new ConductEventLogWriter(scenario.Workspace.ConductEventsLogPath));
        BatchTickSummary? heldTick = null;

        // This workspace-free driver uses the injected synchronous projector, so there
        // is no repository admission-facts worker to warm before either tick.
        Assert.Null(driver.AdmissionFacts.Reader);
        loop.Run(scenario.Kernel, driver, ConductorAutonomyPolicy.Permissive,
            Path.Combine(scenario.Repo, "stop-does-not-exist"), maxIterations: 1,
            onTick: tick => heldTick = tick, persistGoalTick: (_, _) => { });

        Assert.Equal(verifierRuns, scenario.RunCount);
        Assert.Empty(trains);
        Assert.Empty(attempts.GetUnreconciledAttempts(scenario.Goals.Select(goal => goal.Id.Value).ToArray()));
        Assert.NotNull(heldTick);
        foreach (var goal in scenario.Goals)
        {
            Assert.Contains(heldTick.ProgressLines!, line =>
                line.StartsWith("ADMISSION ", StringComparison.Ordinal) &&
                line.Contains($"goal={goal.Id.Value[..8]}", StringComparison.Ordinal) &&
                line.Contains("result=held reason=acceptance-engine-circuit", StringComparison.Ordinal));
            // GOAL reasons are serialized as single tokens with spaces replaced by underscores.
            Assert.Contains(heldTick.ProgressLines!, line =>
                line.StartsWith($"GOAL goal={goal.Id.Value[..8]} ", StringComparison.Ordinal) &&
                line.Contains("result=held", StringComparison.Ordinal) &&
                line.Contains("acceptance_engine_circuit_is_tripped", StringComparison.Ordinal) &&
                line.Contains("acceptance-engine_clear", StringComparison.Ordinal));
        }
        Assert.DoesNotContain(scenario.ReadLog(), record =>
            record.Detail.StartsWith("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal));
        Assert.DoesNotContain(heldTick.ProgressLines!, line =>
            line.StartsWith("ACCEPTANCE_TRAIN ", StringComparison.Ordinal));
        AcceptanceCohortWorkflowTestsMainSuspect.AssertMembersUnrouted(scenario);

        scenario.Circuit.Clear("operator verified main repair");
        Assert.Equal(AcceptanceEngineHealth.Healthy, (await scenario.Circuit.ReadAsync()).Health);
        BatchTickSummary? admittedTick = null;
        loop.Run(scenario.Kernel, driver, ConductorAutonomyPolicy.Permissive,
            Path.Combine(scenario.Repo, "stop-does-not-exist"), maxIterations: 1,
            onTick: tick => admittedTick = tick, persistGoalTick: (_, _) => { });

        Assert.Equal(scenario.Goals.Select(goal => goal.Id).OrderBy(id => id.Value),
            Assert.Single(trains).OrderBy(id => id.Value));
        Assert.NotNull(admittedTick);
        var entry = Assert.Single(admittedTick.ProgressLines!.Where(line =>
            line.StartsWith("ACCEPTANCE_TRAIN ", StringComparison.Ordinal)));
        foreach (var goal in scenario.Goals)
            Assert.Contains(goal.Id.Value[..8], entry, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.ReadLog(), record =>
            record.Detail.StartsWith("ACCEPTANCE_COHORT_ENTRY", StringComparison.Ordinal));
        Assert.DoesNotContain(admittedTick.ProgressLines!, line =>
            line.Contains("reason=acceptance-engine-circuit", StringComparison.Ordinal));
        // Admission reaches only the controlled train runner; the recorded cohort is not rerun.
        Assert.Equal(verifierRuns, scenario.RunCount);
        AcceptanceCohortWorkflowTestsMainSuspect.AssertMembersUnrouted(scenario);
    }
}
