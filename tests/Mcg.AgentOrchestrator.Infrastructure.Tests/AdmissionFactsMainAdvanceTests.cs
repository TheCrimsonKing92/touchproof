using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: unique repository and event-gated derivation.
public sealed class AdmissionFactsMainAdvanceTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void MainAdvances_TickRejectsOldFactsThenConsumesNewFacts()
    {
        using var census = Mcg.AgentOrchestrator.Infrastructure.GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var kernel = new AgentOrchestratorKernel();
        var goal = CreateVerifiedSimpleGoal(kernel, "Main advances");
        using var fixture = new AdditiveConflictGitFixture(goalId: goal.Id);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new AdmissionFactsTickReader(fixture.Repository, "main",
            (inputs, _) => inputs.Select(AdmissionFactsTestData.Fact).ToArray());
        var driver = MakeDriver(getFacts: _ => new(WorkspaceExists: true), isVerificationGateSatisfied: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly, runAcceptanceWithSlot: (_, _) => AcceptanceVerificationSummary.PassedWithNoUnmetCriteria);
        driver.AdmissionFacts.Reader = reader;
        driver.BeginAdmissionFacts([goal], kernel);
        AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Initial facts did not finish.");
        driver.BeginAdmissionFacts([goal], kernel);
        var old = Assert.IsType<GateReadyCandidateProjectionResult.Ready>(driver.ProjectCachedGateReadyCandidate(goal, ConductorAutonomyPolicy.Conservative));
        Assert.Equal(fixture.MainHead, old.Projection.MainRevision);
        AdditiveConflictGitFixture.Git(fixture.Repository, "commit", "--allow-empty", "-m", "Advance main");
        var main = AdditiveConflictGitFixture.Git(fixture.Repository, "rev-parse", "main").Trim();
        Assert.NotEqual(fixture.MainHead, main);
        reader.Derivation.BeforeDerive = (_, _) =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "New-main facts were not released.");
        };
        try
        {
            driver.BeginAdmissionFacts([goal], kernel);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(60)), "New-main derivation did not start.");
            var pending = ConductorBatchLoopTestsStepLedger.OneTick(new(), kernel, driver);
            Assert.Contains(pending.ProgressLines!, line => line.Contains("reason=admission-facts-pending") && line.Contains($"main={main}"));
            Assert.DoesNotContain(pending.ProgressLines!, line => line.Contains("result=admitted"));
            release.Set();
            AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Cancelled derivation did not exit.");
            reader.Derivation.BeforeDerive = null;
            driver.BeginAdmissionFacts([goal], kernel);
            AdmissionFactsTestData.Wait(reader.Derivation.Completion, "New-main facts did not finish.");
            var ready = ConductorBatchLoopTestsStepLedger.OneTick(new(), kernel, driver);
            Assert.Contains("admission_facts=ready:1,pending:0", ConductorBatchLoopTestsStepLedger.Phase(ready, "acceptance-admission"));
            Assert.Equal(main, Assert.IsType<AdmissionFacts>(reader.Facts(goal.Id)).Inputs.Main);
        }
        finally { release.Set(); driver.AdmissionFacts.Stop(); AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Worker did not stop."); }
    }
}
