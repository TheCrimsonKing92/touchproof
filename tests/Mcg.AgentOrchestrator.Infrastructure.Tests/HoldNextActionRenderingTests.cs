using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: private kernels, workspace/database, injected probes and local writers.
public sealed class HoldNextActionRenderingTests
{
    private static readonly DateTimeOffset Start = DateTimeOffset.UnixEpoch;

    [Theory]
    [InlineData(ConductorHoldOwner.AcceptanceQueue, "conductor", "next-tick")]
    [InlineData(ConductorHoldOwner.BackgroundAttempt, "conductor", "gate-reconciliation")]
    [InlineData(ConductorHoldOwner.WorkerCapacity, "unmapped", "unmapped")]
    public void Recorder_UntypedHold_RecordsOwnerNextAction(ConductorHoldOwner owner, string actor, string verb)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("held goal");
        HashSet<GoalId> changed = [];

        ConductorLandingBlockerRecorder.Observe(kernel, goal,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "waiting") { Owner = owner },
            Start, changed);

        Assert.Equal(new GoalLandingBlocker($"untyped/{owner}", "waiting", $"untyped/{owner}:",
            Start, actor, verb), goal.LandingBlocker);
        Assert.Equal(goal.Id, Assert.Single(changed));
    }

    [Theory]
    [InlineData(ConductorHoldOwner.AcceptanceQueue)]
    [InlineData(ConductorHoldOwner.BackgroundAttempt)]
    public void Recorder_UnknownTypedEvidence_PreservesUnmappedFallback(ConductorHoldOwner owner)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("typed hold");
        var held = new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "unknown typed reason")
        {
            Owner = owner,
            Decision = new PolicyDecisionRecord("acceptance", "Hold", 1, "unknown-evidence", "held", [])
        };

        ConductorLandingBlockerRecorder.Observe(kernel, goal, held, Start, []);

        var blocker = Assert.IsType<GoalLandingBlocker>(goal.LandingBlocker);
        Assert.Equal("acceptance/unknown-evidence", blocker.Kind);
        Assert.Equal("unmapped", blocker.NextActor);
        Assert.Equal("unmapped", blocker.NextVerb);
    }

    [Theory]
    [InlineData(ConductorHoldOwner.AcceptanceQueue, "next-tick")]
    [InlineData(ConductorHoldOwner.BackgroundAttempt, "gate-reconciliation")]
    public async Task RecordedBlocker_ConsoleAndStatus_RenderConductorNextAction(ConductorHoldOwner owner, string verb)
    {
        using var fixture = new ConductorVerbStartTests.Fixture();
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("held verified goal");
        ConductorLandingBlockerRecorder.Observe(kernel, goal,
            new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "waiting") { Owner = owner },
            Start, []);
        Assert.NotNull(goal.LandingBlocker);
        var snapshot = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(item => item with { Status = GoalStatus.Verified }).ToArray()
        });

        var detail = OwnerConsoleLandingBlockerText.Detail(kernel.GetGoal(goal.Id), TimeZoneInfo.Utc);
        Assert.Equal($"untyped/{owner} since 00:00; owner: {owner}; next: conductor {verb}; detail: waiting", detail);
        Assert.DoesNotContain("not mapped", detail!);

        await InfrastructureTestSupport.CreateMigratedStateRepository(fixture.Workspace.SqliteStatePath)
            .SaveAsync(kernel);
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CliConductorCommand.Run(["conductor", "status"], fixture.Workspace,
            lockProbe: new ConductorVerbStartTests.FixedProbe(null),
            bootTime: () => Start, mainCommit: () => "def456", output: output, error: error));
        Assert.Equal("", error.ToString());
        Assert.Contains($"    landing-blocker: untyped/{owner} since={Start:O} next=conductor verb={verb} detail=waiting",
            output.ToString());
        Assert.DoesNotContain("next=unmapped", output.ToString());
    }

    [Theory]
    [InlineData("solo-evidence-awaiting-consumption", "solo evidence consumption", "conductor", "wait for the next tick")]
    [InlineData("solo-evidence-invalid: InvalidDataException: solo-duplicate-test-row", "solo acceptance failure reproduction",
        "Developer", "route the Developer because the failure reproduced")]
    [InlineData("solo-acceptance-retry-cap", "solo acceptance correction retry cap", "operator",
        "review the solo acceptance correction evidence")]
    [InlineData("dispatch-start-aborted-repeatedly",
        "retry admission failed twice in a row with the same failure text; the unstarted dispatch was discarded each time", "operator",
        "read the failure text in the task note, then run adjudicate --goal <goal-prefix> <task-number> route --cause <cause> --text-file <note> --evidence <reference>")]
    [InlineData("solo-receipt-absent", "solo attempt evidence recovery", "operator",
        "inspect the named attempt's results and re-gate the current candidate")]
    [InlineData("solo-evidence-awaiting-consumption: wrapped", "unmapped", "unmapped", "unmapped")]
    [InlineData("solo-acceptance-retry-cap: wrapped", "unmapped", "unmapped", "unmapped")]
    [InlineData("SOLO-DUPLICATE-TEST-ROW", "unmapped", "unmapped", "unmapped")]
    public void SoloHold_Detail_PreservesEvidenceAndAppendsNextAction(
        string reason, string mechanism, string owner, string action)
    {
        var goal = new AgentOrchestratorKernel().CreateGoal("solo hold");
        string? escalation = null;
        var handler = new SoloAcceptanceFailureHandler(
            () => null,
            (_, _, _, _, _, _) => throw new InvalidOperationException("Hold must not record another failure"),
            () => Start,
            _ => throw new InvalidOperationException("Hold must not read routing context"),
            (_, _, _) => throw new InvalidOperationException("Hold must not charge a retry"),
            (_, _, _, _) => throw new InvalidOperationException("Hold must not route a failure"),
            (recordedGoal, state, detail) =>
            {
                Assert.Same(goal, recordedGoal);
                Assert.Equal(GoalLifecycleState.AcceptanceFailed, state);
                escalation = detail;
            });

        var result = handler.Hold(goal, goal.Id.Value[..8], ConductorAutonomyPolicy.Permissive, reason);

        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(result.Outcome);
        Assert.Equal($"Solo acceptance correction held: {reason}; attempt=absent; evidence=absent; sha256=absent" +
            $"; mechanism: {mechanism}; owner: {owner}; next action: {action}", held.Reason);
        Assert.Equal(held.Reason, escalation);
        var decision = Assert.IsType<PolicyDecisionRecord>(held.Decision);
        Assert.Equal(reason, decision.DiscriminatingEvidence);
        Assert.Equal(held.Reason, decision.Reason);
        if (owner != "unmapped") Assert.DoesNotContain("unmapped", held.Reason);
    }

    [Theory]
    [InlineData("cross-generation-count:candidate=10,minimum=12,main=12,deleted=0", true)]
    [InlineData("cross-generation-count:candidate=10,minimum=12,main=14,deleted=2", false)]
    [InlineData("cross-generation-count:candidate=10,minimum=12,main=12", false)]
    [InlineData("cross-generation-count:candidate=10,minimum=12,main=12,deleted=00", false)]
    [InlineData("cross-generation-count:candidate=10,minimum=12,main=12,deleted=0x", false)]
    [InlineData("cross-generation-staleness:reason=main-generation-changed,deleted=0", false)]
    public void StructuralDetail_OnlyExplicitZeroRemovals_ExplainsRealCountGap(string receipt, bool expected)
    {
        var coverage = new TestCoverageInvariantResult(false, "count shortfall", [receipt], [],
            "structural-coverage-failed", []);

        var lines = StructuralCoverageFailureDetail.Format(coverage, 10);

        if (expected) Assert.Contains(StructuralCoverageFailureDetailTests.ZeroRemovalMeaning, lines);
        else Assert.DoesNotContain(StructuralCoverageFailureDetailTests.ZeroRemovalMeaning, lines);
        Assert.Contains($"receipt: {receipt}", lines);
    }

    [Fact]
    public void StructuralDetail_ZeroRemovalBeyondCap_ExplainsOnce()
    {
        const string receipt = "cross-generation-count:candidate=10,minimum=12,main=12,deleted=0";
        var coverage = new TestCoverageInvariantResult(false, "count shortfall",
            ["cross-generation-staleness:reason=main-generation-changed", receipt, receipt], [],
            "structural-coverage-failed", []);

        var lines = StructuralCoverageFailureDetail.Format(coverage, 1);

        Assert.Equal(StructuralCoverageFailureDetailTests.ZeroRemovalMeaning,
            Assert.Single(lines.Where(line => line == StructuralCoverageFailureDetailTests.ZeroRemovalMeaning)));
        Assert.DoesNotContain($"receipt: {receipt}", lines);
        Assert.Contains("omitted: 2 more receipts not listed", lines);
    }
}
