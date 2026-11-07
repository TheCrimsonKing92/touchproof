using System.Xml.Linq;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static AttributedFailureDeveloperRouteTests;

// Parallel-safe: fixture owns all I/O and its injected dispatch runner.
public sealed class SoloAttributedFailureRouteCollisionTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task Complete_PassedCollisions_RoutesDeveloperOnNextTick()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce(collidingPassedRows: true);
        var artifact = Assert.Single(Assert.Single(fixture.Proof().Checks).Artifacts);
        var results = XDocument.Load(artifact.Path).Descendants("UnitTestResult").ToArray();
        var passed = results.Where(row => row.Attribute("outcome")!.Value == "Passed").ToArray();
        Assert.Equal(3, passed.Length);
        Assert.Single(passed.Select(row => row.Attribute("testName")!.Value).Distinct());
        Assert.Equal(3, passed.Select(row => row.Attribute("testId")!.Value).Distinct().Count());
        Assert.Equal(2, results.Count(row => row.Attribute("outcome")!.Value == "NotExecuted"));
        Assert.Single(results.Where(row => row.Attribute("outcome")!.Value == "Failed"));
        var completion = fixture.Complete();
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(completion.Outcome);
        Assert.Contains("solo-evidence-awaiting-consumption", held.Reason);

        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 2);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive with { AutomaticRetryOperatorRoutingHoldSeconds = 0 });

        var retry = Assert.Single(MarkerRetries(fixture.Goal));
        var task = Assert.Single(fixture.Goal.Tasks);
        Assert.Equal(AgentRole.Developer, task.RequiredRole);
        Assert.Equal(task.Id, Assert.Single(fixture.Starts));
        Assert.Equal(RetryCause.NewTestFinding, task.PendingRetryCause);
        Assert.Contains("reproduced " + SoloAttributedFailureRouteFixture.First + ": exact first line", retry.Message);
        Assert.Contains(artifact.Path, retry.Message);
        Assert.DoesNotContain("second line", retry.Message);
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(1, task.CriterionRetryCount);
    }
}
