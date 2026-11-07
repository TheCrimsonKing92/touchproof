using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its workspace, attempt artifacts, owner and TRX.
public sealed class SoloGateDeclaredTestRemovalsTests : IDisposable
{
    private const string First = "Example.Tests.RemovedTests.First";
    private const string Second = "Example.Tests.RemovedTests.Second";
    private const string Undeclared = "Example.Tests.RemovedTests.Undeclared";
    private const string Retained = "Example.Tests.RetainedTests.Runs";
    private readonly string _root = CreateTempDirectory();

    private static string Objective =>
        $"## What to build\n- test-removal: `{Undeclared}`\n## Acceptance criteria\n- test-removal: `{First}`\n- test-removal: `{Second}`";

    [Fact]
    public void ParallelSoloAttempt_OptionsCarryObjectiveDeclaredRemovals()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = GoalLifecycleCommands.CreateAndActivateSimpleGoal(kernel, AgentCatalog.Default().Agents, Objective);
        try
        {
            var coordinator = new ConductorParallelAcceptanceAttemptCoordinator(
                _root, TrunkBranchName.Default, runInline: true, acquireStableSlotLease: (_, _) => null);
            var candidate = ConductorParallelAcceptanceCandidate.Create(goal, 0, ["tests/ExampleTests.cs"], "branch", "main");
            AcceptanceRunExecutionOptions? captured = null;

            var result = coordinator.Evaluate(candidate, ConductorAutonomyPolicy.Conservative,
                (runCandidate, _, _, _, options) =>
                {
                    captured = options;
                    return ConductorParallelAcceptanceRunResult.Accepted(
                        runCandidate, AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);
                });

            Assert.Equal(ConductorParallelAcceptanceAttemptDecisionKind.Completed, result.Kind);
            Assert.NotNull(captured);
            Assert.Equal([goal.Id], captured.AttemptMemberGoalIds);
            Assert.Equal([new MemberDeclaredTestRemoval(goal.Id, First), new(goal.Id, Second)], captured.MemberTestRemovals);
        }
        finally
        {
            DotnetBuildEnvironmentManager.TryDeleteGoalArtifacts(goal.Id);
        }
    }

    [Fact]
    public async Task SoloOwner_WithoutCriteriaFile_CreditsObjectiveDeclaredRemovalsOnly()
    {
        var goal = new AgentOrchestratorKernel().CreateGoal(Objective);
        var options = SoloLandingGateExecutionOptions.Create(goal);
        var criteriaPath = Path.Combine(_root, ".orchestrator", "goal-acceptance-criteria.json");
        Assert.False(File.Exists(criteriaPath));
        await using var owner = AcceptanceExecutionOwners.CreateAttempt(_root,
            options: options with { ResultsPrefix = Path.Combine(_root, "results", "solo") });
        var source = new GoalAcceptanceVerifier(
            (string[] _, string _, TimeSpan _, CancellationToken _) =>
                throw new InvalidOperationException("Resolving sanctioned removals must not launch a command"));
        var verifier = new GoalAcceptanceVerifier(source, (IAcceptanceRunExecutionContext)owner);

        var sanctioned = verifier.SanctionedTestRemovalsForTests(_root);

        Assert.Equal([First, Second], sanctioned);
        Assert.DoesNotContain(Undeclared, sanctioned);
        Assert.False(File.Exists(criteriaPath));
        var declaredOnly = Evaluate(sanctioned, First, Second);
        Assert.True(declaredOnly.Passed, declaredOnly.Summary);
        Assert.Empty(declaredOnly.MissingTests);
        var undeclaredAbsent = Evaluate(sanctioned, First, Second, Undeclared);
        Assert.False(undeclaredAbsent.Passed);
        Assert.Equal(["cross-generation-count:candidate=1,minimum=2,main=4,deleted=2"], undeclaredAbsent.MissingTests);
        foreach (var identity in new[] { First, Second })
            Assert.Contains($"source=declared-test-removal,identity={identity},status=corroborated,credit=1",
                undeclaredAbsent.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain($"identity={Undeclared},", undeclaredAbsent.Summary, StringComparison.Ordinal);
    }

    private TestCoverageInvariantResult Evaluate(string[] sanctioned, params string[] absentTests)
    {
        var trx = Path.Combine(_root, "coverage.trx");
        File.WriteAllText(trx, $"""
            <TestRun><TestDefinitions>
              <UnitTest id="1" name="{Retained}"><TestMethod className="Example.Tests.RetainedTests" name="Runs" /></UnitTest>
            </TestDefinitions><Results>
              <UnitTestResult testId="1" testName="{Retained}" outcome="Passed" />
            </Results></TestRun>
            """);
        var mainTests = new[] { Retained }.Concat(absentTests).ToArray();
        var locations = mainTests.ToDictionary(identity => identity, _ => "tests/Example.Tests/RetainedTests.cs");
        return TestCoverageInvariant.Evaluate([Retained], [new TestPartitionCoverage("solo", true, [trx])],
            mainTests, [], mainDiscoveredTestSourceFiles: locations, sanctionedRemovedTests: sanctioned);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
