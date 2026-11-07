using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = RepositoryTestReachTests.RepositoryFixture;

// Parallel-safe: each row owns its repository, worktrees, stores and runner.
public sealed class MergeTrainReachRuleTests : AcceptanceCohortWorkflowTests
{
    private const string TestFile = Fixture.TestDirectory + "/UnchangedReachTests.cs";
    private const string Identity = "UnchangedReachTests.Fails";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MergeTrainRedAttributionReplayScenarioTestsReachRule(bool shared)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            Fixture.Populate(repo);
            Fixture.WriteAt(repo, TestFile, "public class UnchangedReachTests { private ChangedSource source; " +
                (shared ? "private OtherSource other; " : "") + "[Xunit.Fact] public void Fails() { } }");
            for (var index = 0; index < 5; index++)
                Fixture.WriteAt(repo, Extra(index), $"public class Extra{index} {{ }}");
            RunGit(repo, "add", "src", "tests");
            RunGit(repo, "commit", "-m", "Add reach base with assertion source");
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Reach member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                if (index == 0)
                {
                    CreateWorktreeCandidate(repo, goal.Id, Fixture.Source, "public class ChangedSource { public int Value => 1; }");
                    for (var extra = 0; extra < 5; extra++)
                        CreateWorktreeCandidate(repo, goal.Id, Extra(extra), $"public class Extra{extra} {{ public int Value => 1; }}");
                }
                else if (index == 1)
                    CreateWorktreeCandidate(repo, goal.Id, Fixture.Other, "public class OtherSource { public int Value => 1; }");
                CreateWorktreeCandidate(repo, goal.Id, Fixture.TestDirectory + $"/Evidence/Member{index}Tests.cs",
                    $"public class Member{index}Tests {{ [Xunit.Fact] public void Runs() {{ }} }}");
                return goal;
            }).ToArray();
            var paths = MergeTrainReachAttributionTests.Trx(repo, [Identity], "assertion failed");
            // The top repository frame is the test's own unchanged assertion. There
            // is no production stack frame from which path ownership could be guessed.
            var document = XDocument.Load(paths[0]);
            var error = document.Descendants().Single(element => element.Name.LocalName == "ErrorInfo");
            error.Add(new XElement(error.Name.Namespace + "StackTrace", $"   at {Identity}() in /_/repo/{TestFile}:line 1"));
            document.Save(paths[0]);
            var red = new AcceptanceVerificationResult(false, false, 1, "train failed",
                Checks: [new AcceptanceCheckResult("train", false, 1, "train failed")], TestResultPaths: paths);
            var passing = new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("remainder", true, 0, null)], TestResultPaths: [WritePassingTrx(repo, "pass.trx")]);
            var verifier = new SequenceAcceptanceVerifier([red, shared ? red : passing]);
            var calls = 0;
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks)
            {
                MergeTrainBisectionRunner = (_, _, _, _) => { calls++; return null; }
            };
            var selection = ProjectTrainSelection(driver, goals);
            Assert.Equal(6, selection.Members[0].LandingPaths.Count(path => path.StartsWith("src/", StringComparison.Ordinal)));
            Assert.All(selection.Members, member => Assert.DoesNotContain(TestFile, member.LandingPaths));
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            var ejection = Assert.Single(result.Ejections);
            Assert.Equal(goals[shared ? 2 : 0].Id, ejection.GoalId);
            Assert.Equal(shared ? MergeTrainEjectionReason.RedNewestMember : MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
            if (shared) Assert.NotEqual(0, calls);
            else Assert.Equal(0, calls);
            var events = File.ReadAllLines(OrchestratorWorkspace.ForDirectory(repo).ConductEventsLogPath)
                .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
                .Where(record => record.EventKind == "train-attribution").ToArray();
            if (shared) Assert.Empty(events);
            else
            {
                Assert.NotEmpty(events);
                Assert.All(events, record => Assert.Contains("attribution=reach", record.Detail));
                Assert.DoesNotContain(result.Ejections, item => item.GoalId == goals[2].Id);
            }
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private static string Extra(int index) => $"src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/Extra{index}.cs";
}
