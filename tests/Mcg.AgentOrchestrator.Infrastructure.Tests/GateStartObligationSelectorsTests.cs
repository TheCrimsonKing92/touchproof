using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static AcceptanceSelectedExecutionFixture;
using static AcceptanceSelectedExecutionObligationTests;

// Parallel-safe: owns a unique workspace/database/build root; never starts a process.
public sealed class GateStartObligationSelectorsTests
{
    [Theory]
    [InlineData("solo")]
    [InlineData("train")]
    [InlineData("cohort")]
    public void GateOptionsIncludeSecondMemberObligation(string kind)
    {
        var root = InfrastructureTestSupport.CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var first = kernel.CreateGoal("First gate member without obligations");
            var second = kernel.CreateGoal("Second gate member with an obligation");
            kernel = GateTestObligationIntakeTests.Seed(kernel, second, ["X"]);
            first = kernel.GetGoal(first.Id);
            second = kernel.GetGoal(second.Id);
            var goals = new[] { first, second };
            AcceptanceRunExecutionOptions options;
            if (kind == "solo") options = SoloLandingGateExecutionOptions.Create(second, kernel.HumanInputRequests);
            else if (kind == "train")
            {
                var workspace = OrchestratorWorkspace.ForDirectory(root);
                InfrastructureTestSupport.SetUpStateDatabase(workspace.SqliteStatePath);
                var verifier = new GoalAcceptanceVerifier((string[] _, string _, TimeSpan _, CancellationToken _) =>
                    throw new InvalidOperationException("Options test must not launch a command"));
                var driver = new ConductorDriver(kernel, workspace, verifier, AgentCatalog.Default().Agents,
                    WorkerProfileCatalog.Default(), cleanupHooks: new GoalWorktreeCleanupHooks
                    { BuildStorageRoot = new(Path.Combine(root, "build-storage")) });
                var members = goals.Select((goal, index) => new MergeTrainMemberBinding(goal.Id,
                    new string((char)('a' + index), 40), new string((char)('a' + index), 40),
                    ["tests/MemberTests.cs"], ["member"], ChangeRiskTier.Behavior,
                    ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected", new string('d', 40))).ToArray();
                options = driver.CreateMergeTrainGateExecutionOptions(
                    MergeTrainIdentity.Create(members, new string('e', 40), new string('f', 40), "manifest-v1"),
                    members, ConductorDriver.TrainGateRunIdentity(members.Select(member => member.GoalId.Value)),
                    goals.ToDictionary(goal => goal.Id));
            }
            else
            {
                var bindings = goals.Select((goal, index) => new AcceptanceCohortMemberBinding(goal.Id,
                    new string((char)('a' + index), 40), new string((char)('a' + index), 40),
                    ["tests/MemberTests.cs"], ["member"], ChangeRiskTier.Behavior,
                    ConductorTransitionDecision.Auto, "Clean", "NoConflictsDetected")).ToArray();
                options = ConductorDriver.CreateCohortGateExecutionOptions(
                    new ConductEventLogWriter(Path.Combine(root, "conduct.jsonl")),
                    AcceptanceCohortIdentity.Create(bindings, new string('c', 40), new string('d', 40), "manifest-v1"),
                    bindings, gateTestObligationSelectors: GateTestObligationEffectiveSelectors.ForMembers(goals,
                        bindings.Select(binding => binding.GoalId), kernel.HumanInputRequests));
            }
            Assert.Equal(new[] { "X" }, options.GateTestObligationSelectors);
            var session = WithObligations(options.GateTestObligationSelectors);
            var lane = Assert.Single(session.Transform(Lanes), lane => lane.Name == "Balanced 2");
            Assert.Equal("(FullyQualifiedName=Candidate.X)", lane.Filter);
            Assert.Equal(new[] { "Candidate.X" }, Observe(session).SelectedExecution!.ObligationClasses);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
