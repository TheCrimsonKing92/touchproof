using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: lane plans, owner roots, stores and the substituted runner are fixture-local.
public sealed class SelectedExecutionBackstopLandingIntakeTests
{
    [Theory]
    [InlineData("attempt-state", false)]
    [InlineData("attempt-state", true)]
    [InlineData("attempt-owner", false)]
    [InlineData("attempt-owner", true)]
    [InlineData("lane-plan", false)]
    [InlineData("lane-plan", true)]
    public async Task LandingReadsItsGateRecordBeforeQueuing(string shape, bool applied)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var owner = Path.Combine(fixture.Home, "owner");
        Directory.CreateDirectory(Path.Combine(owner, ".git"));
        var worktree = Path.Combine(owner, ".orchestrator-worktrees", "candidate");
        Directory.CreateDirectory(worktree);
        const string attempt = "gate-attempt";
        const string goal = "landed-goal";
        var path = shape switch
        {
            "attempt-state" => Path.Combine(fixture.Home, "acceptance-gate-attempts", goal, attempt + AcceptanceGateLanePlan.ArtifactSuffix),
            "attempt-owner" => Path.Combine(owner, ".orchestrator", "acceptance-gate-attempts", goal, attempt + AcceptanceGateLanePlan.ArtifactSuffix),
            _ => Path.Combine(fixture.Home, "shared-cohort-or-train", attempt + AcceptanceGateLanePlan.ArtifactSuffix)
        };
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var plan = new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, attempt, [], [])
            { SelectedExecution = applied ? SelectedExecutionBackstopFixture.Applied : AcceptanceSelectedExecutionRecord.Disabled };
        File.WriteAllText(path, plan.ToJson());
        // Solo, home and PR receipts carry an attempt; cohort/train and their recovery
        // receipts carry the exact shared lane-plan path, without a goal-local artifact.
        var receipt = new ConductorLandingReceipt(goal, [], "main-1",
            GateAttemptId: shape == "lane-plan" ? null : attempt,
            GateLanePlanPath: shape == "lane-plan" ? path : null, PreviousMainSha: "main-0");
        fixture.Coordinator.NoteLanding(receipt, worktree);
        var landed = Assert.Single((await fixture.Store.ReadAsync())!.RangeLandings);
        Assert.Equal(applied, landed.Applied);
        Assert.False(landed.RecordUnreadable);
        await fixture.Finish();
        Assert.Equal(applied ? 1 : 0, fixture.Calls.Count);
        Assert.DoesNotContain(fixture.Lines, line => line.Contains("landing-record-unreadable"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mismatched")]
    [InlineData("unidentified")]
    public async Task MissingOrMismatchedGateEvidenceIsReportedWithoutQueuing(string evidence)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var path = Path.Combine(fixture.Home, "attempt" + AcceptanceGateLanePlan.ArtifactSuffix);
        if (evidence == "mismatched")
            File.WriteAllText(path, new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, "different-attempt", [], [])
                { SelectedExecution = SelectedExecutionBackstopFixture.Applied }.ToJson());
        fixture.Coordinator.NoteLanding(new("goal", [], "main-1", GateAttemptId: evidence == "unidentified" ? null : "attempt",
            GateLanePlanPath: evidence == "unidentified" ? null : path, PreviousMainSha: "main-0"), fixture.Home);
        await fixture.Finish();
        Assert.Empty(fixture.Calls);
        Assert.Empty((await fixture.Store.ReadAsync())!.Uncovered);
        Assert.Null((await fixture.Store.ReadAsync())!.OpenMiss);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.Contains(fixture.Lines, line => line.Contains("result=landing-record-unreadable goal=goal commit=main-1"));
    }
}
