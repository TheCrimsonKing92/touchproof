using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;

// Parallel-safe: in-memory goals and results only; no worker, git or shared state.
public sealed class AdmissionFactsPendingAdmissionTests(ITestOutputHelper output) : ConductorBatchLoopTests(output)
{
    [Theory]
    [InlineData("executed")]
    [InlineData("held")]
    [InlineData("escalated")]
    [InlineData("done")]
    public void ExistingResult_PendingFactsPreserveOutcomeAndSlot(string kind)
    {
        var kernel = new AgentOrchestratorKernel();
        var reconciled = CreateVerifiedSimpleGoal(kernel, "Already reconciled this tick");
        var waiting = CreateVerifiedSimpleGoal(kernel, "Still awaiting facts");
        var policy = ConductorAutonomyPolicy.Permissive;
        ConductorAdvanceOutcome outcome = kind switch
        {
            "executed" => new ConductorAdvanceOutcome.Executed(GoalLifecycleState.Verified, "Landed before prep"),
            "held" => new ConductorAdvanceOutcome.Held(GoalLifecycleState.Verified, "Landing mutation boundary"),
            "escalated" => new ConductorAdvanceOutcome.Escalated(GoalLifecycleState.Verified, "Acceptance failed"),
            "done" => new ConductorAdvanceOutcome.Done(GoalLifecycleState.CleanedUp),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        var recorded = new ParallelLandingOutcome(new(reconciled.Id.Value, reconciled.Id.Value[..8],
            policy.Name, outcome), SlotIndex: 2);
        var results = new Dictionary<string, ParallelLandingOutcome> { [reconciled.Id.Value] = recorded };
        const string inputs = "branch=new-branch;main=new-main";
        var candidates = new[] { reconciled, waiting }.Select(goal => new ConductorSpeculativeAcceptanceCandidate(
            goal.Id, new GateReadyCandidateProjectionResult.Excluded(GateReadyCandidateExclusionReason.AdmissionFactsPending)
            { HoldReason = inputs })).ToArray();
        var progress = new List<string>();

        var eligible = AdmissionFactsPendingAdmission.Apply([reconciled, waiting], candidates,
            results, policy, tick: 1, progress.Add);

        Assert.Same(recorded, results[reconciled.Id.Value]);
        Assert.Same(outcome, results[reconciled.Id.Value].Result.Outcome);
        Assert.Equal(2, results[reconciled.Id.Value].SlotIndex);
        Assert.Same(reconciled, Assert.Single(eligible));
        Assert.Equal(2, results.Count);
        var pending = results[waiting.Id.Value].Result;
        Assert.True(AdmissionFactsPendingAdmission.IsPending(pending));
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(pending.Outcome);
        Assert.Contains($"goal={waiting.Id.Value} inputs={inputs}", held.Reason);
        Assert.Equal($"ADMISSION tick=1 result=held reason=admission-facts-pending goal={waiting.Id.Value[..8]} inputs={inputs}",
            Assert.Single(progress));
    }
}
