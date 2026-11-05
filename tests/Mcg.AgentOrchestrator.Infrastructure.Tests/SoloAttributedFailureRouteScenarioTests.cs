using System.Text.Json;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static AttributedFailureDeveloperRouteTests;

// Parallel-safe: fixture owns all I/O and its injected dispatch runner.
public sealed class SoloAttributedFailureRouteScenarioTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    public async Task ProducerCompletionReloadRoutesOneExactDeveloperCorrection()
    {
        using var fixture = new SoloAttributedFailureRouteFixture();
        await fixture.Produce();
        // Use the saved-result contract before completion, then reload goal state
        // from an independently owned SQLite store before the conductor consumes it.
        var artifact = new ConductorParallelAcceptanceRunArtifact("accepted", null, null, null, null,
            fixture.Summary, fixture.Context.CandidateSha, fixture.Context.MainSha, null, null);
        var resultPath = Path.Combine(fixture.Root, "acceptance-result.json");
        File.WriteAllBytes(resultPath, JsonSerializer.SerializeToUtf8Bytes(artifact));
        var saved = JsonSerializer.Deserialize<ConductorParallelAcceptanceRunArtifact>(File.ReadAllBytes(resultPath))!;
        Assert.Equal(fixture.Summary.SoloFailureReceipt, saved.Acceptance!.SoloFailureReceipt);
        fixture.Complete(saved.Acceptance);
        await fixture.Restart();
        var driver = fixture.Driver();
        driver.BeginTick(fixture.Kernel, 2);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        var retry = Assert.Single(MarkerRetries(fixture.Goal));
        Assert.Equal(fixture.Goal.Tasks.Single().Id, Assert.Single(fixture.Starts));
        Assert.Equal(RetryCause.NewTestFinding, fixture.Goal.Tasks.Single().PendingRetryCause);
        Assert.Contains("reproduced " + SoloAttributedFailureRouteFixture.First + ": exact first line", retry.Message);
        Assert.Contains("reproduced " + SoloAttributedFailureRouteFixture.Long + ": " + new string('x', 1997) + "...", retry.Message);
        Assert.DoesNotContain("second line", retry.Message);
        Assert.DoesNotContain(new string('x', 2001), retry.Message);
        Assert.Equal(1, fixture.Goal.AutomaticAcceptanceRetryCount);
        Assert.Equal(1, fixture.Goal.Tasks.Single().CriterionRetryCount);
        driver.BeginTick(fixture.Kernel, 3);
        driver.AdvanceOnce(fixture.Goal, ConductorAutonomyPolicy.Permissive);
        Assert.Single(fixture.Starts);
        Assert.Single(MarkerRetries(fixture.Goal));
    }

    [Fact]
    public void OlderAcceptanceRecordsRemainReadableAndCannotAuthorizeSoloRouting()
    {
        var older = JsonSerializer.Deserialize<AcceptanceFailureSummary>("""
            { "OccurredAt":"2026-11-04T00:00:00Z", "FailedChecks":["solo tests"],
              "BranchHeadSha":"candidate", "MainHeadSha":"main" }
            """);
        Assert.NotNull(older);
        Assert.Null(older.SoloFailureReceipt);
        Assert.Null(older.AttemptId);
        Assert.Null(AttributedFailureDeveloperRoute.ReadAcceptanceFailure(older));
    }
}
