using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: unique repository and event gates; the tick never waits for the worker.
public sealed class AdmissionFactsPendingTickTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Fact]
    public void BlockedCandidate_TickHoldsItAndAdmitsFinishedCandidates()
    {
        using var census = GateLoadContextProbe.PushLiveGateOccupantProbe(() => []);
        var kernel = new AgentOrchestratorKernel();
        var ready = CreateVerifiedSimpleGoal(kernel, "Ready facts");
        var blocked = CreateVerifiedSimpleGoal(kernel, "Blocked facts");
        using var fixture = new AdditiveConflictGitFixture(goalId: blocked.Id);
        AdditiveConflictGitFixture.Git(fixture.Repository, "update-ref", $"refs/heads/{GoalWorktrees.BranchName(ready.Id)}", fixture.OriginalHead);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var reader = new AdmissionFactsTickReader(fixture.Repository, "main",
            (inputs, _) => inputs.Select(AdmissionFactsTestData.Fact).ToArray());
        reader.Begin([ready], _ => []);
        AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Ready facts did not finish.");
        reader.Derivation.BeforeDerive = (input, _) =>
        {
            if (input.GoalId != blocked.Id) return;
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(60)), "The candidate derivation was not released.");
        };
        var admitted = new List<GoalId>();
        var driver = MakeDriver(getFacts: _ => new(WorkspaceExists: true), isVerificationGateSatisfied: _ => true,
            classifyRisk: _ => ChangeRiskTier.DocsOnly, runAcceptanceWithSlot: (goal, _) =>
            { admitted.Add(goal.Id); return AcceptanceVerificationSummary.PassedWithNoUnmetCriteria; });
        driver.AdmissionFacts.Reader = reader;
        try
        {
            reader.Begin([ready, blocked], _ => []);
            Assert.True(entered.Wait(TimeSpan.FromSeconds(60)), "The candidate derivation did not block.");
            var pendingTimeline = blocked.Timeline.Count;
            var tick = Task.Run(() => ConductorBatchLoopTestsStepLedger.OneTick(new(), kernel, driver));
            AdmissionFactsTestData.Wait(tick, "The admission tick waited for the blocked derivation.");
            Assert.False(release.IsSet);
            Assert.Contains(tick.Result.ProgressLines!, line => line.Contains($"reason=admission-facts-pending goal={blocked.Id.Value[..8]} inputs=branch="));
            Assert.Contains(ready.Id, admitted);
            Assert.DoesNotContain(blocked.Id, admitted);
            Assert.Equal(pendingTimeline, blocked.Timeline.Count);
            Assert.Contains("admission_facts=ready:1,pending:1", ConductorBatchLoopTestsStepLedger.Phase(tick.Result, "acceptance-admission"));
        }
        finally { release.Set(); reader.Derivation.Stop(); AdmissionFactsTestData.Wait(reader.Derivation.Completion, "Blocked worker did not exit."); }
    }
}
