using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: lane plans, owner roots and stores are fixture-local.
public sealed class SelectedExecutionBackstopLandingIntakeTests
{
    [Theory]
    [InlineData("attempt-state", false)]
    [InlineData("attempt-state", true)]
    [InlineData("attempt-owner", false)]
    [InlineData("attempt-owner", true)]
    [InlineData("lane-plan", false)]
    [InlineData("lane-plan", true)]
    public async Task LandingReadsItsGateRecord(string shape, bool applied)
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
        Assert.Equal(applied ? plan.SelectedExecution!.Lanes.SelectMany(lane => lane.SkippedClasses)
            .Distinct(StringComparer.Ordinal).ToArray() : [], landed.SkippedClasses);
        Assert.DoesNotContain(fixture.Lines, line => line.Contains("landing-record-unreadable"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("mismatched")]
    [InlineData("unidentified")]
    public async Task UnreadableGateEvidenceRetainsUnreadableLanding(string evidence)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var path = Path.Combine(fixture.Home, "attempt" + AcceptanceGateLanePlan.ArtifactSuffix);
        if (evidence == "mismatched")
            File.WriteAllText(path, new AcceptanceGateLanePlan(AcceptanceGateLanePlan.CurrentSchemaVersion, "different-attempt", [], [])
                { SelectedExecution = SelectedExecutionBackstopFixture.Applied }.ToJson());
        fixture.Coordinator.NoteLanding(new("goal", [], "main-1", GateAttemptId: evidence == "unidentified" ? null : "attempt",
            GateLanePlanPath: evidence == "unidentified" ? null : path, PreviousMainSha: "main-0"), fixture.Home);

        var landing = Assert.Single((await fixture.Store.ReadAsync())!.RangeLandings);
        Assert.True(landing.RecordUnreadable);
        Assert.False(landing.Applied);
        Assert.Contains(fixture.Lines, line => line.Contains("result=landing-record-unreadable goal=goal commit=main-1"));
    }

    [Theory]
    [InlineData("Enforce")]
    [InlineData("Shadow")]
    [InlineData("Off")]
    public async Task OnlyAppliedLandingsCopyDistinctSkippedClassesFromAllLanes(string mode)
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        var record = mode == "Off" ? AcceptanceSelectedExecutionRecord.Disabled :
            SelectedExecutionBackstopFixture.Applied with
            {
                Mode = mode, Applied = mode == "Enforce", StructuralCoverageScoped = mode == "Enforce",
                Lanes = [new("first", "narrowed", [], ["Ns.C", "Ns.D"], null),
                    new("second", "skipped", [], ["Ns.D", "Ns.c"], null)]
            };
        await fixture.Land(record: record);
        var landing = Assert.Single((await fixture.Store.ReadAsync())!.RangeLandings);
        Assert.Equal(mode == "Enforce" ? new[] { "Ns.C", "Ns.D", "Ns.c" } : [], landing.SkippedClasses);
    }

    [Fact]
    public async Task LegacySnapshotWithoutSkippedClassesReadsAndCannotCreateMiss()
    {
        using var fixture = new SelectedExecutionBackstopFixture();
        const string legacy = """
            {"RangeStartCommit":"main-0","LastLandingOrdinal":1,"Landings":[
              {"Ordinal":1,"GoalId":"legacy","Commit":"main-1","Applied":true,"RecordUnreadable":false}]}
            """;
        await fixture.Events.AppendAsync(new(SelectedExecutionBackstopStore.EventType, null, "landing", "landing", null,
            legacy, fixture.Now));
        var landing = Assert.Single((await fixture.Store.ReadAsync())!.RangeLandings);
        Assert.Empty(landing.SkippedClasses);
        Assert.False(SelectedExecutionBackstopMissClassifier.Classify(["Candidate.X.Method"], [landing]).IsMiss);
        await fixture.DetectMiss();
        var state = (await fixture.Store.ReadAsync())!;
        Assert.Null(state.Request);
        Assert.Null(state.OpenMiss);
        Assert.Empty(await fixture.Items.ListAsync());
        Assert.False(File.Exists(AcceptanceSelectedExecutionSessionSuspension.MarkerPath(fixture.Home)));
        Assert.Contains(fixture.Lines, line => line.Contains("result=gate-no-miss "));
    }
}
