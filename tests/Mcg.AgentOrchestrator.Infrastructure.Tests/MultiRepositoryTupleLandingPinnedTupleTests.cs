using System.Xml.Linq;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each test owns its repositories, receipt/TRX files and execution-scoped git override.
public sealed class MultiRepositoryTupleLandingPinnedTupleTests
{
    private const string Selector = "record.TestReadInvalidFixtures";

    [Fact]
    public void Execute_CrosswalkObligationSatisfiedByReceiptTrx_LandsBothRepositoriesWithoutCompensation()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var (kernel, goal, receipt) = Prepare(fixture);
        var repositories = fixture.Repositories;
        var order = new List<string>();
        using var git = LandingExecutor.UseGitRunner((root, args) =>
        {
            var result = GitCli.Run(root, args);
            if (result.Succeeded && args.Length >= 2 && args[0] == "update-ref" && args[1] == "refs/heads/main")
                order.Add(root == repositories.Home.CheckoutPath ? "default" : "crosswalk");
            return result;
        });
        var landing = new MultiRepositoryTupleLanding(repositories.Workspace, repositories.Registry);
        var homeCalls = 0;
        var result = landing.Execute(goal, GoalLifecycleState.Verified, receipt, () =>
        {
            homeCalls++;
            Assert.Equal(new[] { "crosswalk" }, order);
            Assert.NotEqual(receipt.TupleCanonical, fixture.Tuple().Canonical);
            Assert.Null(HomeLandingAdmissibility.Evaluate(kernel, goal, repositories.Workspace));
            return LandingExecutor.Execute(kernel, goal, repositories.Workspace);
        }, () => null, () => HomeLandingAdmissibility.Evaluate(kernel, goal, repositories.Workspace));

        Assert.Null(result.Hold);
        Assert.NotNull(result.Record);
        Assert.Equal(1, homeCalls);
        Assert.True(result.HomeLanding!.MainAdvanced, result.HomeLanding.Message);
        Assert.Equal(new[] { "crosswalk", "default" }, order);
        Assert.Equal(order, result.Record.Order);
        Assert.Equal("fixture secondary change", repositories.Crosswalk.Git("show", "main:example.txt"));
        Assert.Equal("fixture home change", repositories.Home.Git("show", "main:example.txt"));
        Assert.Equal(repositories.Crosswalk.Git("rev-parse", "main"), result.Record.LandedCommits["crosswalk"]);
        Assert.Equal(repositories.Home.Git("rev-parse", "main"), result.Record.LandedCommits["default"]);
        Assert.NotNull(landing.Store.ReadLanding(goal.Id));
        Assert.Null(landing.Store.ReadFailure(goal.Id));
        Assert.Null(MultiRepositoryLandingTuplePin.For(goal.Id));
    }

    [Fact]
    public void Execute_ReceiptStaleForPreflightTuple_RefusesBeforeAnyRepositoryChanges()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var (_, goal, receipt) = Prepare(fixture);
        var repositories = fixture.Repositories;
        var homeBefore = repositories.Home.Git("rev-parse", "main");
        var secondaryBefore = repositories.Crosswalk.Git("rev-parse", "main");
        GoalRepositoryWorktreeProvisionerFixture.CommitChange(fixture.SecondaryWorktree, "new candidate\n");
        Assert.NotEqual(receipt.TupleCanonical, fixture.Tuple().Canonical);
        var homeCalls = 0;
        var mutationCalls = 0;
        var preflights = 0;
        var landing = new MultiRepositoryTupleLanding(repositories.Workspace, repositories.Registry);

        var result = landing.Execute(goal, GoalLifecycleState.Verified, receipt,
            () => { homeCalls++; throw new InvalidOperationException("Stale receipt reached home landing."); },
            () => { mutationCalls++; return null; },
            () => { preflights++; return null; });

        Assert.NotNull(result.Hold);
        Assert.StartsWith("multi-repository-acceptance-stale: tuple", result.Hold.Reason);
        Assert.Null(result.Record);
        Assert.Equal(0, homeCalls);
        Assert.Equal(0, mutationCalls);
        Assert.Equal(0, preflights);
        Assert.Equal(homeBefore, repositories.Home.Git("rev-parse", "main"));
        Assert.Equal(secondaryBefore, repositories.Crosswalk.Git("rev-parse", "main"));
        Assert.Null(landing.Store.ReadAttempt(goal.Id));
        Assert.Null(landing.Store.ReadFailure(goal.Id));
        Assert.Null(landing.Store.ReadLanding(goal.Id));
        Assert.Null(fixture.Gate.Store.TryRead(goal.Id));
        Assert.Null(MultiRepositoryLandingTuplePin.For(goal.Id));
    }

    [Fact]
    public void HomeLandingAdmissibility_OutsideTupleLanding_UsesFreshTupleAfterCrosswalkAdvances()
    {
        using var fixture = new MultiRepositoryAcceptanceGateFixture();
        var (kernel, goal, receipt) = Prepare(fixture);
        Assert.Null(HomeLandingAdmissibility.Evaluate(kernel, goal, fixture.Repositories.Workspace));

        fixture.Repositories.Crosswalk.Git("merge", "--ff-only", GoalRepositoryWorktreeLayout.BranchName(goal.Id, "crosswalk"));

        Assert.NotEqual(receipt.TupleCanonical, fixture.Tuple().Canonical);
        Assert.Null(MultiRepositoryLandingTuplePin.For(goal.Id));
        var refusal = HomeLandingAdmissibility.Evaluate(kernel, goal, fixture.Repositories.Workspace);
        Assert.NotNull(refusal);
        Assert.Contains("repository crosswalk: no gate result for the current candidate tuple", refusal);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, MultiRepositoryAcceptanceReceipt Receipt) Prepare(
        MultiRepositoryAcceptanceGateFixture fixture)
    {
        var snapshot = fixture.Repositories.Kernel.ExportSnapshot();
        var kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with { Goals = [snapshot.Goals.Single() with
        {
            GateTestObligations = [new("crosswalk-obligation", fixture.Goal.AuthoritativeBrief.Version, [Selector], null,
                "receipt TRX", DateTimeOffset.UnixEpoch, RepositoryKey: "crosswalk")]
        }] });
        var goal = kernel.GetGoal(fixture.Goal.Id);
        Assert.Equal("crosswalk", Assert.Single(goal.GateTestObligations).RepositoryKey);
        var store = fixture.Gate.Store;
        var attempt = Guid.NewGuid().ToString("N");
        var trx = Path.Combine(fixture.Repositories.Home.Root, "crosswalk.trx");
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        new XDocument(new XElement(ns + "TestRun",
            new XElement(ns + "Results", new XElement(ns + "UnitTestResult",
                new XAttribute("testId", "go"), new XAttribute("testName", Selector), new XAttribute("outcome", "Passed"))),
            new XElement(ns + "TestDefinitions", new XElement(ns + "UnitTest",
                new XAttribute("id", "go"), new XAttribute("name", Selector),
                new XElement(ns + "TestMethod", new XAttribute("adapterTypeName", "executor://go-test/v1"),
                    new XAttribute("className", "example.org/crosswalk/record"), new XAttribute("codeBase", "record"),
                    new XAttribute("name", "TestReadInvalidFixtures")))))).Save(trx);
        var secondary = store.WriteEvidence(goal.Id, attempt, "crosswalk", new { Passed = true, TestResultPaths = new[] { trx } });
        var home = store.WriteEvidence(goal.Id, attempt, "default", new { Passed = true, TestResultPaths = Array.Empty<string>() });
        var bases = new Dictionary<string, string>
        {
            ["default"] = fixture.Repositories.Home.Git("rev-parse", "main"),
            ["crosswalk"] = fixture.Repositories.Crosswalk.Git("rev-parse", "main")
        };
        var receipt = new MultiRepositoryAcceptanceReceipt(goal.Id, fixture.Tuple().Canonical,
            [new("default", true, home, null), new("crosswalk", true, secondary, null)], DateTimeOffset.UtcNow, bases);
        store.Write(receipt, goal.Repositories!.Repositories);
        GoalOperationJournal.AcceptancePassed(fixture.Repositories.Home.CheckoutPath, goal, "fixture:acceptance",
            GoalRepositoryWorktreeProvisionerFixture.Run(fixture.HomeWorktree, "rev-parse", "HEAD"), bases["default"]);
        return (kernel, goal, receipt);
    }
}
