using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using Fixture = RepositoryTestReachTests.RepositoryFixture;

// Parallel-safe: each scenario owns its repository, stores and deterministic runners.
public sealed class MergeTrainCauseReplayTests : AcceptanceCohortWorkflowTests
{
    private const string LinkedGuard = "AcceptancePolicyShardPlannerLinkedSourceTests.DeclaredLinks_ExactlyMatchConsumerTable";
    private const string Landing = "GoalWorktreeTestsAcceptanceLanding.CliConductCompletedGoalLandsThroughPersistentRunnerWithoutCommandTransaction";
    private const string Shared = "SharedReachTests.Fails";
    private const string LandingSource = Fixture.TestDirectory + "/AcceptanceLandingSource.cs";
    private const string OtherSource = "src/Mcg.AgentOrchestrator.Core/Domain/ReplayOtherSource.cs";
    private const string Projects = Fixture.TestDirectory + "/OwnerConsole/OwnerConsole.csproj";
    private const string Retention = Fixture.TestDirectory + "/StorageRetention/StorageRetention.csproj";

    [Fact]
    public void Case6LinkedSourceGuard_EjectsMiddleProjectFileMember() => Replay("projects", scenario =>
    {
        AssertAttributed(scenario, 1, "reach", LinkedGuard);
        Assert.Contains(Projects, scenario.Selection.Members[1].LandingPaths);
        Assert.Contains(Retention, scenario.Selection.Members[1].LandingPaths);
        Assert.DoesNotContain(scenario.Selection.Members[2].LandingPaths, path => path.EndsWith(".csproj"));
        Assert.Empty(scenario.SoloCalls);
    });

    [Fact]
    public void Case2AcceptanceLanding_EjectsEarlierSourceMember() => Replay("landing", scenario =>
    {
        AssertAttributed(scenario, 0, "reach", Landing);
        Assert.Contains(Fixture.Source, scenario.Selection.Members[0].LandingPaths);
        Assert.Contains(LandingSource, scenario.Selection.Members[0].LandingPaths);
        Assert.All(scenario.Selection.Members[2].LandingPaths, path => Assert.StartsWith("tests/", path));
        Assert.Equal(3, scenario.Selection.Members[2].LandingPaths.Count);
        Assert.Empty(scenario.SoloCalls);
    });

    [Fact]
    public void ReachNamesNone_SoloSweepEjectsOnlyReproducingMember() => Replay("solo", scenario =>
    {
        AssertAttributed(scenario, 1, "member-recheck", Shared);
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.SoloCalls);
    });

    [Fact]
    public void NoSoloReproduction_EjectsNoneWithInconclusiveFallback() => Replay("interaction", scenario =>
    {
        Assert.Empty(scenario.Result.Ejections);
        Assert.Null(scenario.Result.Receipt);
        Assert.Empty(scenario.Result.MemberResults);
        Assert.Contains("reason=inconclusive", scenario.Result.Detail);
        Assert.Contains("fallback=ordinary", scenario.Result.Detail);
        Assert.Equal(1, scenario.Verifier.RunCount);
        Assert.Equal(scenario.Goals.Select(goal => goal.Id), scenario.SoloCalls);
        var store = new MergeTrainAcceptanceStore(Path.Combine(scenario.Repo, ".orchestrator", "merge-train-acceptance.db"));
        var red = Assert.IsType<MergeTrainReceipt>(scenario.Result.RecordedReceipt);
        Assert.Empty(store.ReadAttemptEjections(red.GateAttemptId!));
        Assert.DoesNotContain(scenario.Result.Ejections, item => item.Reason == MergeTrainEjectionReason.RedNewestMember);
        AssertOutcomeEvent(scenario, "train-inconclusive", "inconclusive", Shared);
    });

    [Theory]
    [InlineData("projects", "reach", LinkedGuard)]
    [InlineData("solo", "member-recheck", Shared)]
    [InlineData("interaction", "inconclusive", Shared)]
    public void ConductEvents_NameDecidingRuleAndFailingTests(string mode, string rule, string identity) =>
        Replay(mode, scenario => AssertOutcomeEvent(scenario,
            rule == "inconclusive" ? "train-inconclusive" : "member-ejected", rule, identity));

    [Theory]
    [InlineData("config/settings.json", "File.ReadAllText(\"config/settings.json\")", true)]
    [InlineData("config/settings.json", "File.ReadAllText(Path.Combine(\"config\", \"settings.json\"))", true)]
    [InlineData("config/settings.json", "Directory.EnumerateFiles(root, \"*.json\", SearchOption.AllDirectories)", true)]
    [InlineData("config/settings.json", "new StreamReader(\"config/settings.json\")", true)]
    [InlineData("docs/settings.json", "File.ReadAllText(Path.Combine(\"config\", \"settings.json\"))", false)]
    public void DataReaders_ReachOnlyMatchingPaths(string changed, string read, bool reached)
    {
        using var fixture = new Fixture();
        fixture.Write("config/settings.json", "{}");
        fixture.Write(changed, "{}");
        fixture.Write(Fixture.TestDirectory + "/DataReaderTests.cs",
            $"public class DataReaderTests {{ [Xunit.Fact] public void Reads() {{ var data = {read}; }} }}");
        var members = new[] { Member('a', changed), Member('b', Fixture.Other), Member('c', "docs/unrelated.md") };
        var receipt = Receipt(fixture.Root, members, "DataReaderTests.Reads");
        var result = MergeTrainClassReachAttribution.TryAttribute(receipt, fixture.Root, members, out _);
        if (reached) Assert.Same(members[0], result);
        else Assert.Null(result);
    }

    [Fact]
    public void MultipleFailingClasses_IntersectReachRatherThanRequireExclusiveOwners()
    {
        using var fixture = new Fixture();
        fixture.Write(Fixture.TestDirectory + "/AlphaTests.cs",
            "public class AlphaTests { private ChangedSource first; private OtherSource second; [Xunit.Fact] public void Fails() { } }");
        fixture.Write(Fixture.TestDirectory + "/BetaTests.cs",
            "public class BetaTests { private ChangedSource first; private Third third; [Xunit.Fact] public void Fails() { } }");
        const string third = "src/Mcg.AgentOrchestrator.Infrastructure/Third.cs";
        fixture.Write(third, "public class Third { }");
        var members = new[] { Member('a', Fixture.Source), Member('b', Fixture.Other), Member('c', third) };
        var receipt = Receipt(fixture.Root, members, "AlphaTests.Fails", "BetaTests.Fails");
        Assert.Null(MergeTrainReachAttribution.TryAttribute(receipt.GateTestResultPaths, fixture.Root, members, out _));
        Assert.Same(members[0], MergeTrainClassReachAttribution.TryAttribute(receipt, fixture.Root, members, out var classes));
        Assert.Equal(new[] { "AlphaTests", "BetaTests" }, classes);
        var unknown = members[2] with { LandingPaths = ["src/Unindexed/Unknown.cs"] };
        Assert.Null(MergeTrainClassReachAttribution.TryAttribute(receipt, fixture.Root, [members[0], members[1], unknown], out _));
    }

    [Theory]
    [InlineData("missing-class")]
    [InlineData("unreadable-trx")]
    [InlineData("split-reach")]
    public void UnresolvedOrSplitEvidence_DoesNotNameAMember(string mode)
    {
        using var fixture = new Fixture();
        fixture.Write(Fixture.TestDirectory + "/AlphaTests.cs",
            "public class AlphaTests { private ChangedSource source; [Xunit.Fact] public void Fails() { } }");
        fixture.Write(Fixture.TestDirectory + "/BetaTests.cs",
            "public class BetaTests { private OtherSource source; [Xunit.Fact] public void Fails() { } }");
        var members = new[] { Member('a', Fixture.Source), Member('b', Fixture.Other), Member('c', "docs/unrelated.md") };
        var receipt = Receipt(fixture.Root, members, "AlphaTests.Fails",
            mode == "missing-class" ? "MissingTests.Fails" : "BetaTests.Fails");
        if (mode == "unreadable-trx") File.WriteAllText(receipt.GateTestResultPaths[0], "<TestRun");
        Assert.Null(MergeTrainClassReachAttribution.TryAttribute(receipt, fixture.Root, members, out var classes));
        Assert.Empty(classes);
    }

    private void Replay(string mode, Action<Scenario> assert)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            Fixture.Populate(repo);
            Fixture.WriteAt(repo, Projects, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Fixture.WriteAt(repo, Retention, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
            Fixture.WriteAt(repo, Fixture.TestDirectory + "/AcceptancePolicyShardPlannerLinkedSourceTests.cs", """
                public class AcceptancePolicyShardPlannerLinkedSourceTests {
                    private static string MainDirectory => Path.Combine(root, "tests/Mcg.AgentOrchestrator.Infrastructure.Tests");
                    [Xunit.Fact] public void DeclaredLinks_ExactlyMatchConsumerTable() {
                        foreach (var project in Directory.EnumerateFiles(MainDirectory, "*.csproj", SearchOption.AllDirectories))
                            XDocument.Load(project);
                    }
                }
                """);
            Fixture.WriteAt(repo, Fixture.TestDirectory + "/GoalWorktreeTestsAcceptanceLanding.cs",
                "public class GoalWorktreeTestsAcceptanceLanding { private AcceptanceLandingSource source; " +
                "[Xunit.Fact] public void CliConductCompletedGoalLandsThroughPersistentRunnerWithoutCommandTransaction() { } }");
            // Integration tests can exercise shared test-host source as well as shipping
            // source. Its change must contribute reach even though it lives under tests/.
            Fixture.WriteAt(repo, LandingSource, "public class AcceptanceLandingSource { }");
            // Use a separate owned subsystem so all three candidates are train-eligible.
            Fixture.WriteAt(repo, OtherSource, "public class ReplayOtherSource { }");
            Fixture.WriteAt(repo, Fixture.TestDirectory + "/SharedReachTests.cs",
                "public class SharedReachTests { private ChangedSource first; private ReplayOtherSource second; [Xunit.Fact] public void Fails() { } }");
            for (var index = 0; index < 5; index++)
                Fixture.WriteAt(repo, Extra(index), $"public class Extra{index} {{ }}");
            RunGit(repo, "add", "src", "tests");
            RunGit(repo, "commit", "-m", "Add replay source and reader contracts");
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Cause replay member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                if (index == 0)
                {
                    CreateWorktreeCandidate(repo, goal.Id, Fixture.Source, "public class ChangedSource { public int Value => 1; }");
                    for (var extra = 0; extra < 5; extra++)
                        CreateWorktreeCandidate(repo, goal.Id, Extra(extra), $"public class Extra{extra} {{ public int Value => 1; }}");
                    if (mode == "landing")
                        CreateWorktreeCandidate(repo, goal.Id, LandingSource,
                            "public class AcceptanceLandingSource { public int Value => 1; }");
                }
                else if (index == 1 && mode == "projects")
                {
                    foreach (var project in new[] { Projects, Retention })
                        CreateWorktreeCandidate(repo, goal.Id, project,
                            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><Compile Include=\"../AssemblyTempRedirect.cs\" /></ItemGroup></Project>");
                }
                else if (index == 1)
                    CreateWorktreeCandidate(repo, goal.Id, OtherSource, "public class ReplayOtherSource { public int Value => 1; }");
                else
                    foreach (var name in new[] { "Train", "TrainVerdict", "TrainLanding" })
                        CreateWorktreeCandidate(repo, goal.Id, Fixture.TestDirectory + $"/AcceptanceCohortWorkflowTests{name}.cs",
                            $"public class AcceptanceCohortWorkflowTests{name} {{ [Xunit.Fact] public void Runs() {{ }} }}");
                return goal;
            }).ToArray();
            var identity = mode == "projects" ? LinkedGuard : mode == "landing" ? Landing : Shared;
            var redPath = MergeTrainFocusedRecheckTests.WriteTrx(repo, "primary.trx", [(identity, "Failed")]);
            var red = new AcceptanceVerificationResult(false, false, 1, "train failed",
                Checks: [new AcceptanceCheckResult("train", false, 1, "train failed")], TestResultPaths: [redPath]);
            var passing = new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("remainder", true, 0, null)], TestResultPaths: [WritePassingTrx(repo, "passed.trx")]);
            var verifier = new SequenceAcceptanceVerifier([red, passing]);
            var soloCalls = new List<GoalId>();
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks)
            {
                MergeTrainBisectionRunner = (_, _, _, _) => null,
                MergeTrainRecheckDependencySelector = (_, _) => new(false, [], "fixture keeps full remainder gate"),
                MergeTrainMemberRunner = (member, main, selection, _) =>
                {
                    soloCalls.Add(member.GoalId);
                    Assert.Equal(new[] { "SharedReachTests" }, selection.Identities);
                    var fails = mode == "solo" && member.GoalId == goals[1].Id;
                    var path = MergeTrainFocusedRecheckTests.WriteTrx(repo, member.GoalId.Value + ".trx", [(Shared, fails ? "Failed" : "Passed")]);
                    return new("solo-replay", main, new(ConductorCohortFocusedPassKind.Executed,
                        fails ? [Shared] : [], fails ? ["focused"] : [], 1, !fails, [path], new string('b', 40)));
                }
            };
            var selection = ProjectTrainSelection(driver, goals);
            Assert.Equal(goals.Select(goal => goal.Id), selection.Members.Select(member => member.GoalId));
            Assert.All(selection.Members, member => Assert.NotEmpty(member.LandingPaths));
            var result = driver.RunMergeTrain(selection, goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            assert(new(repo, goals, selection, result, verifier, soloCalls));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private static void AssertAttributed(Scenario scenario, int culprit, string rule, string identity)
    {
        var ejection = Assert.Single(scenario.Result.Ejections);
        Assert.Equal(scenario.Goals[culprit].Id, ejection.GoalId);
        Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
        Assert.DoesNotContain(scenario.Result.Ejections, item => item.GoalId == scenario.Goals[2].Id);
        var receipt = Assert.IsType<MergeTrainReceipt>(scenario.Result.Receipt);
        Assert.Equal(MergeTrainGateOutcome.Passed, receipt.Outcome);
        Assert.Equal(scenario.Goals.Where((_, index) => index != culprit).Select(goal => goal.Id),
            receipt.Identity.Members.Select(member => member.GoalId));
        AssertOutcomeEvent(scenario, "member-ejected", rule, identity);
    }

    private static void AssertOutcomeEvent(Scenario scenario, string phase, string rule, string identity)
    {
        var records = File.ReadAllLines(OrchestratorWorkspace.ForDirectory(scenario.Repo).ConductEventsLogPath)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!).ToArray();
        var record = Assert.Single(records.Where(record => record.Detail.Contains($"phase={phase} ", StringComparison.Ordinal)));
        Assert.Contains($"rule={rule}", record.Detail);
        Assert.Contains($"failing_tests={identity}", record.Detail);
        Assert.DoesNotContain(records, record => record.Detail.Contains("rule=position", StringComparison.Ordinal));
    }

    private static string Extra(int index) => $"src/Mcg.AgentOrchestrator.Infrastructure/Workspaces/Extra{index}.cs";
    private static MergeTrainMemberBinding Member(char id, params string[] paths) =>
        MergeTrainPrefixBisectionTests.Members(3)[id - 'a'].WithRebasedHead(new string('b', 40)) with { LandingPaths = paths };
    private static MergeTrainReceipt Receipt(string repo, IReadOnlyList<MergeTrainMemberBinding> members, params string[] identities) =>
        new("reach-replay", MergeTrainIdentity.Create(members, new string('a', 40), new string('b', 40), "manifest"),
            MergeTrainGateOutcome.Failed, DateTimeOffset.UnixEpoch, 0, ["train"], 1,
            [MergeTrainFocusedRecheckTests.WriteTrx(repo, "reach.trx", identities.Select(identity => (identity, "Failed")).ToArray())]);
    private sealed record Scenario(string Repo, Goal[] Goals, ConductorMergeTrainSelection Selection,
        ConductorMergeTrainRunResult Result, SequenceAcceptanceVerifier Verifier, List<GoalId> SoloCalls);
}
