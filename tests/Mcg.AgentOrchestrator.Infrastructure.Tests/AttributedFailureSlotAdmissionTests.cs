using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using static AttributedFailureAutoRouteScenarioTests;
using static AttributedFailureDeveloperRouteTests;

// Parallel-safe: the shared scenario owns its repository, storage root and scripted admission.
public sealed class AttributedFailureSlotAdmissionTests : AcceptanceCohortWorkflowTests
{
    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public void AttemptOneAdmissionDeferred_KeepsCulpritVerified()
    {
        WithTrainScenario(true, [true, false, false, false], scenario =>
        {
            Assert.Equal(4, scenario.AdmissionIdentities.Count);
            var deferredIdentity = scenario.AdmissionIdentities[1];
            Assert.NotEqual(scenario.AdmissionIdentities[0], deferredIdentity);
            Assert.All(scenario.AdmissionIdentities.Skip(1), identity => Assert.Equal(deferredIdentity, identity));
            Assert.Equal(2, scenario.DelayCount);
            Assert.Equal(1, scenario.VerifierRunCount);
            Assert.Equal($"outcome={CohortStableSlotAcquisitionRounds.DeferredVerdict} train={deferredIdentity}",
                scenario.Run.Detail);
            Assert.Null(scenario.Run.Receipt);
            Assert.Equal(scenario.FailedReceipt.ReceiptId, scenario.Run.RecordedReceipt?.ReceiptId);
            Assert.All(scenario.Goals, goal =>
            {
                Assert.Equal(GoalStatus.Verified, goal.Status);
                var held = Assert.IsType<ConductorAdvanceOutcome.Held>(scenario.Run.MemberResults[goal.Id.Value].Outcome);
                Assert.Equal(GoalLifecycleState.Verified, held.State);
                Assert.Empty(MarkerRetries(goal));
            });
        });
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [Trait("Category", "CrossTick")]
    [InlineData(true)]
    [InlineData(false)]
    public void BusyAdmissionRoundThenGrant_RoutesCulpritOnce(bool ownership)
    {
        WithTrainScenario(ownership, [true, false, true], scenario =>
        {
            Assert.Equal(3, scenario.AdmissionIdentities.Count);
            Assert.NotEqual(scenario.AdmissionIdentities[0], scenario.AdmissionIdentities[1]);
            Assert.Equal(scenario.AdmissionIdentities[1], scenario.AdmissionIdentities[2]);
            Assert.Equal(1, scenario.DelayCount);
            Assert.Equal(2, scenario.VerifierRunCount);
            Assert.DoesNotContain(CohortStableSlotAcquisitionRounds.DeferredVerdict, scenario.Run.Detail);
            Assert.Equal(MergeTrainGateOutcome.Passed, scenario.Run.Receipt?.Outcome);
            scenario.AssertRoutesCulpritOnce();
        });
    }
}
