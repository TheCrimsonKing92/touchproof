using System.Text.Json;
using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: fixture owns repository, stores, worktrees and runner.
public sealed class MergeTrainPrefixBisectionTrainTests : AcceptanceCohortWorkflowTests
{
    private const string TestName = MergeTrainPrefixBisectionTests.TestName;
    private static readonly string[] Paths =
    [
        "tests/Mcg.AgentOrchestrator.Core.Tests/OldestTests.cs",
        "tests/Mcg.AgentOrchestrator.Infrastructure.Tests/MiddleTests.cs",
        "tests/Mcg.AgentOrchestrator.Dashboard.Tests/NewestTests.cs"
    ];

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void PrefixCulpritAloneIsEjectedRoutedAndExcludedFromSurvivorReceipt(int guilty)
    {
        WithTrain(guilty, "complete", train =>
        {
            var ejection = Assert.Single(train.Result.Ejections);
            Assert.Equal(train.Goals[guilty].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedAttributedMember, ejection.Reason);
            Assert.Contains("bisection=", ejection.Detail);
            var survivors = train.Goals.Where((_, index) => index != guilty).Select(goal => goal.Id).ToArray();
            Assert.Equal(survivors, train.Result.Receipt!.Identity.Members.Select(member => member.GoalId));
            Assert.Equal(2, train.Verifier.RunCount);
            Assert.Equal(Paths.Where((_, index) => index != guilty), train.Verifier.ChangedFiles[1]);
            Assert.Equal(new[] { ConductorAcceptanceCohortAttributedMembers.Key(train.Goals[guilty].Id,
                train.Selection.Members[guilty].CandidateRevision) }, train.Driver.ReadTrainImplicatedMemberKeys());
            var attribution = Assert.Single(Events(train.Repo, "train-attribution"));
            Assert.Contains("attribution=bisection", attribution.Detail);
            Assert.Equal(train.Goals[guilty].Id.Value, attribution.GoalId);
            AssertFailureDetails(train.Goals[guilty], train.Calls.Select(call => call.Prefix.Count));
            Assert.All(train.Goals.Where((_, index) => index != guilty), goal => Assert.Null(goal.LatestAcceptanceFailure));
            Assert.Equal(train.Calls.Count, Events(train.Repo, "train-bisection").Length);
            Assert.All(train.Calls, call =>
            {
                Assert.Equal(new[] { TestName }, call.Tests);
                Assert.Equal(train.FailedReceipt.Identity.ObservedMainRevision, call.MainRevision);
                Assert.Equal(train.FailedReceipt.Identity.Members.Take(call.Prefix.Count).Select(member => (member.GoalId, member.CandidateRevision)),
                    call.Prefix.Select(member => (member.GoalId, member.CandidateRevision)));
            });
            Assert.All(Events(train.Repo, "train-bisection"), record =>
            {
                Assert.Contains(TestName, record.Detail);
                Assert.Contains("verdict=", record.Detail);
                Assert.Contains("trx=prefix-", record.Detail);
            });
        });
    }

    [Theory]
    [InlineData("flake", "inconclusive-flake")]
    [InlineData("main-red", "main-baseline-red")]
    public void InconclusiveOrRedMainRecordsReasonAndEjectsNobody(string mode, string outcome)
    {
        WithTrain(0, mode, train =>
        {
            Assert.Empty(train.Result.Ejections);
            Assert.Null(train.Result.Receipt);
            Assert.Contains("fallback=ordinary", train.Result.Detail);
            Assert.Equal(1, train.Verifier.RunCount);
            Assert.All(train.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            var outcomes = Events(train.Repo, "train-bisection-outcome");
            Assert.Equal(3, outcomes.Length);
            Assert.All(outcomes, record =>
            {
                Assert.Contains(outcome, record.Detail);
                Assert.Contains(TestName, record.Detail);
                Assert.Contains(record.GoalId, train.Goals.Select(goal => goal.Id.Value));
            });
            Assert.Empty(Events(train.Repo, "member-ejected"));
            var calls = train.Calls.Count;
            var replay = train.Driver.RunMergeTrain(train.Selection, train.Goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            Assert.Empty(replay.Ejections);
            Assert.Equal(calls, train.Calls.Count);
            Assert.Equal(calls, Events(train.Repo, "train-bisection").Length);
            Assert.Equal(mode == "flake" ? 4 : 1, calls);
        });
    }

    [Theory]
    [InlineData("unreadable")]
    [InlineData("incomplete")]
    [InlineData("skipped")]
    [InlineData("exception")]
    public void UnavailableMachineryKeepsNewestFallbackAndRecordsReason(string mode)
    {
        WithTrain(0, mode, train =>
        {
            var ejection = Assert.Single(train.Result.Ejections);
            Assert.Equal(train.Goals[2].Id, ejection.GoalId);
            Assert.Equal(MergeTrainEjectionReason.RedNewestMember, ejection.Reason);
            Assert.Equal(2, train.Verifier.RunCount);
            Assert.All(Events(train.Repo, "train-bisection-outcome"), record =>
            {
                Assert.Contains("prefix-bisection unavailable", record.Detail);
                Assert.Contains(mode == "exception" ? "runner-failed:IOException" : "unusable-result", record.Detail);
            });
            Assert.Equal(3, Events(train.Repo, "train-bisection-outcome").Length);
            Assert.Single(train.Calls);
        });
    }

    [Theory]
    [InlineData("guard")]
    [InlineData("not-red")]
    [InlineData("owned")]
    public void EarlierAttributionAndNonRedNeverRunBisection(string mode) =>
        WithTrain(0, mode, train => Assert.Empty(train.Calls));

    [Fact]
    public void ReadOnlyChildEvidenceReplaysWithoutRerunsAndRoutesInParent()
    {
        WithTrain(1, "complete", train =>
        {
            Assert.All(train.Goals, goal => Assert.Equal(GoalStatus.Verified, goal.Status));
            var calls = train.Calls.Count;
            var replay = train.Driver.RunMergeTrain(train.Selection, train.Goals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            Assert.Equal(calls, train.Calls.Count);
            Assert.Equal(train.Goals[1].Id, Assert.Single(replay.Ejections).GoalId);
            AssertFailureDetails(train.Goals[1], train.Calls.Select(call => call.Prefix.Count));
            Assert.Empty(train.Driver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
        }, readOnlyChild: true);
    }

    [Fact]
    public void ParentReconciliationPreservesBisectionNamesAndTrx()
    {
        WithTrain(1, "complete", train =>
        {
            Assert.Equal(train.Goals[1].Id, Assert.Single(train.Driver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive)));
            AssertFailureDetails(train.Goals[1], train.Calls.Select(call => call.Prefix.Count));
            Assert.Empty(train.Driver.ReconcileAttributedTrainFailures(ConductorAutonomyPolicy.Permissive));
        }, readOnlyChild: true);
    }

    private static void AssertFailureDetails(Goal goal, IEnumerable<int> prefixes)
    {
        Assert.Equal(GoalStatus.AcceptanceFailed, goal.Status);
        var failure = Assert.IsType<AttributedFailure>(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(goal.LatestAcceptanceFailure));
        Assert.Equal("train-bisection", failure.Source);
        Assert.Equal(new[] { TestName }, failure.FailingTests);
        var feedback = AttributedFailureDeveloperRoute.BuildFeedback(goal, failure, "signature");
        Assert.Contains(TestName, feedback);
        Assert.All(prefixes, prefix => Assert.Contains($"prefix-{prefix}.trx", feedback));
    }

    private void WithTrain(int guilty, string mode, Action<Scenario> assert, bool readOnlyChild = false)
    {
        var repo = CreateReducedAcceptanceCohortRepository();
        var cleanup = CreateIsolatedCleanupContext(repo);
        try
        {
            AddAcceptanceManifest(repo);
            Directory.CreateDirectory(Path.Combine(repo, "tests"));
            File.WriteAllText(Path.Combine(repo, "tests/UnchangedTests.cs"), "namespace Ns; public class UnchangedTests { }");
            RunGit(repo, "add", "tests/UnchangedTests.cs");
            RunGit(repo, "commit", "-m", "Add unchanged test source");
            var kernel = new AgentOrchestratorKernel();
            var goals = Enumerable.Range(0, 3).Select(index =>
            {
                var goal = CreateCompletedGoal(kernel, $"Reproduction member {index}", repo);
                kernel.RecordGoalRefinement(goal.Id, new RefinedSpec(goal.Objective,
                    ["The full acceptance gate passes"], VerificationClass.TestVerifiable, [], [])
                { AcceptanceGateOwnedAcceptanceCriteria = ["The full acceptance gate passes"] });
                _ = CreateWorktreeCandidate(repo, goal.Id, Paths[index],
                    $"namespace Ns; public class {Path.GetFileNameWithoutExtension(Paths[index])} {{ }}");
                return goal;
            }).ToArray();
            var identity = mode == "guard" ? "Ns.WorkflowDecisionCoverageRatchetTests.EveryUndecidedSite_IsAllowListedByFileAndMember" :
                mode == "owned" ? "Ns.OldestTests.Fails" : TestName;
            var red = Red(repo, identity);
            var passing = new AcceptanceVerificationResult(true, false, 0, null,
                Checks: [new AcceptanceCheckResult("remainder", true, 0, null)],
                TestResultPaths: [WritePassingTrx(repo, "pass.trx")]);
            var verifier = new SequenceAcceptanceVerifier(mode == "not-red" ? [passing] :
                mode == "second-attempt" ? [red, red] : [red, passing]);
            if (mode == "flake")
                new AcceptanceLaneFlakeLedger(repo).Append(new(GoalId.New().Value, "prior", "Remainder", "tree", "first", [],
                    [TestName], "failed", "rerun", [], [], null, "flake", DateTimeOffset.UtcNow.AddDays(-1)));
            var driver = new ConductorDriver(kernel, OrchestratorWorkspace.ForDirectory(repo), verifier,
                AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks);
            var calls = new List<Invocation>();
            driver.MergeTrainBisectionRunner = (prefix, main, tests, _) =>
            {
                calls.Add(new(prefix, main, tests));
                if (mode == "exception") throw new IOException("prefix composition unavailable");
                if (mode == "unreadable") return null;
                if (mode == "incomplete") return new(new([], []), [], "incomplete");
                if (mode == "skipped") return new(new(tests, [new(TestName, "NotExecuted", "skipped", null)]), [], "skipped");
                var failed = mode == "main-red" || prefix.Count > guilty;
                if (mode == "flake") failed = prefix.Count > 0 && calls.Count % 2 == 1;
                return MergeTrainPrefixBisectionTests.Execution(tests, failed, prefix.Count);
            };
            var selection = new ConductorMergeTrainSelection(goals.Select(goal =>
                Assert.IsType<GateReadyCandidateProjectionResult.Ready>(
                    driver.ProjectGateReadyCandidate(goal, ConductorAutonomyPolicy.Permissive)).Projection).ToArray());
            var executingDriver = driver;
            var executingGoals = goals;
            if (readOnlyChild)
            {
                var workspace = OrchestratorWorkspace.ForDirectory(repo);
                new SqliteOrchestratorStateRepository(workspace.SqliteStatePath).SaveAsync(kernel).GetAwaiter().GetResult();
                var child = SqliteOrchestratorStateRepository.OpenReadOnly(workspace.SqliteStatePath)
                    .LoadGoalsAsync(goals.Select(goal => goal.Id).ToArray()).GetAwaiter().GetResult();
                executingGoals = goals.Select(goal => child.GetGoal(goal.Id)).ToArray();
                executingDriver = new ConductorDriver(child, workspace, verifier, AgentCatalog.Default().Agents,
                    WorkerProfileCatalog.Default(), cleanupHooks: cleanup.Hooks)
                { MergeTrainBisectionRunner = driver.MergeTrainBisectionRunner };
            }
            var result = executingDriver.RunMergeTrain(selection, executingGoals, ConductorAutonomyPolicy.Permissive, gateOnly: true);
            var store = new MergeTrainAcceptanceStore(Path.Combine(repo, ".orchestrator/merge-train-acceptance.db"));
            var failed = store.ReadReceiptsSince(DateTimeOffset.MinValue).FirstOrDefault(receipt => receipt.Outcome == MergeTrainGateOutcome.Failed);
            assert(new(repo, goals, driver, selection, result, verifier, calls, failed!));
            AssertNoMergeTrainWorkspaces(repo);
        }
        finally { DeleteDirectory(repo); }
    }

    private static AcceptanceVerificationResult Red(string repo, string identity)
    {
        var path = Path.Combine(repo, "red.trx");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun", new XElement(ns + "Results",
            new XElement(ns + "UnitTestResult", new XAttribute("testName", identity), new XAttribute("outcome", "Failed"),
                new XElement(ns + "Output", new XElement(ns + "ErrorInfo", new XElement(ns + "Message", "train failure"))))),
            new XElement(ns + "ResultSummary", new XAttribute("outcome", "Failed"), new XElement(ns + "Counters",
                new XAttribute("total", 1), new XAttribute("executed", 1),
                new XAttribute("passed", 0), new XAttribute("failed", 1))))).Save(path);
        return new(false, false, 1, "train failed",
            Checks: [new AcceptanceCheckResult("train", false, 1, "train failed")], TestResultPaths: [path]);
    }

    private static ConductEventRecord[] Events(string repo, string kind)
    {
        var path = OrchestratorWorkspace.ForDirectory(repo).ConductEventsLogPath;
        return !File.Exists(path) ? [] : File.ReadAllLines(path)
            .Select(line => JsonSerializer.Deserialize<ConductEventRecord>(line, new JsonSerializerOptions(JsonSerializerDefaults.Web))!)
            .Where(record => record.EventKind == kind).ToArray();
    }

    private sealed record Invocation(IReadOnlyList<MergeTrainMemberBinding> Prefix, string MainRevision, IReadOnlyList<string> Tests);
    private sealed record Scenario(string Repo, Goal[] Goals, ConductorDriver Driver, ConductorMergeTrainSelection Selection,
        ConductorMergeTrainRunResult Result, SequenceAcceptanceVerifier Verifier, List<Invocation> Calls, MergeTrainReceipt FailedReceipt);
}
