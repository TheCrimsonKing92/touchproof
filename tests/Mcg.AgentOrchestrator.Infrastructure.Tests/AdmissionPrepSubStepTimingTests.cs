using System.Text.RegularExpressions;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: unique repository, instance ledger and injected process CPU clock.
public sealed class AdmissionPrepSubStepTimingTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void ReadyFacts_PhaseTimingIncludesInputLookupPolicyAndApprovalSubsteps()
    {
        using var census = Mcg.AgentOrchestrator.Infrastructure.GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Substep timing");
        using var fixture = new AdditiveConflictGitFixture(goalId: goal.Id);
        var reader = new AdmissionFactsTickReader(fixture.Repository, "main",
            (inputs, _) => inputs.Select(AdmissionFactsTestData.Fact).ToArray());
        reader.Begin([goal], _ => []);
        AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Telemetry fixture facts did not finish.");
        var driver = MakeDriver(getFacts: _ => new(WorkspaceExists: true), isVerificationGateSatisfied: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly, runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria,
            runAcceptanceCohort: (_, _, _) => throw new InvalidOperationException("A single candidate cannot form a cohort."));
        driver.AdmissionFacts.Reader = reader;
        var cpu = TimeSpan.Zero;
        var measured = new[] { "projection-inputs", "projection-inputs-frozen", "projection-inputs-refs",
            "projection-inputs-approvals", "projection-inputs-key", "projection-facts-lookup", "projection-policy",
            "approval-inputs", "approval-projections", "approval-facts-lookup" };
        var loop = new ConductorBatchLoop(processCpuTime: () => cpu).WithStepProbe(name =>
        {
            if (measured.Contains(name)) cpu += TimeSpan.FromMilliseconds(7);
        });
        var tick = ConductorBatchLoopTestsStepLedger.OneTick(loop, kernel, driver);
        var line = ConductorBatchLoopTestsStepLedger.Phase(tick, "acceptance-admission");
        Assert.StartsWith("PHASE_TIMING ", line);
        Assert.Contains(" prep_substeps=", line);
        foreach (var name in measured)
        {
            var value = Regex.Match(line, $@"(?:=|,){name}:\d+:(\d+):([1-9]\d*)");
            Assert.True(value.Success, $"Missing wall/CPU/call telemetry for {name}: {line}");
            Assert.True(long.Parse(value.Groups[1].Value) >= 7);
        }
        Assert.Contains("admission_facts=ready:1,pending:0,oldest_pending_ms:0", line);
    }

    [Fact]
    public void PendingCounters_CountDistinctGoalsAndReportOldestAge()
    {
        var ledger = new ConductorTickStepLedger(() => TimeSpan.Zero);
        var pending = GoalId.New();
        using (ledger.Activate())
        {
            ConductorTickStepLedger.CountAdmissionFacts(pending, false, TimeSpan.FromSeconds(3));
            ConductorTickStepLedger.CountAdmissionFacts(pending, false, TimeSpan.FromSeconds(3));
            ConductorTickStepLedger.CountAdmissionFacts(GoalId.New(), true, TimeSpan.Zero);
        }
        Assert.Contains("admission_facts=ready:1,pending:1,oldest_pending_ms:3000", ledger.FormatAdmission(TimeSpan.Zero));
    }
}
