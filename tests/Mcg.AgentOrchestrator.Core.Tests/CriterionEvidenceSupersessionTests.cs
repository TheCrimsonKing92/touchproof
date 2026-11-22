using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceSupersessionTests
{
    [Fact]
    public void Record_ValidObservationCannotReplaceSupersededDuty()
    {
        const string criterion = "Observe the activation";
        var scope = new CriterionObservationScope(CriterionObservationKind.Runtime, "Activated runtime", "goal-runtime");
        var clock = new FakeClock();
        var seed = new AgentOrchestratorKernel(clock);
        var draft = seed.CreateGoal("Supersede a published duty");
        seed.RecordGoalRefinement(draft.Id, new RefinedSpec("Observe", [criterion], VerificationClass.RealWorldDependent, [], [])
        {
            OperatorOwnedAcceptanceCriteria = [criterion],
            CriterionEvidenceDeclarations = [new(criterion, CriterionEvidencePhase.PostLanding, scope, Atomic: true)]
        });
        var kernel = AgentOrchestratorKernel.FromSnapshot(
            new OrchestratorSnapshot([seed.ExportGoalSnapshot(draft.Id) with { Status = GoalStatus.Completed }], []), clock);
        var goal = kernel.GetGoal(draft.Id);
        var publication = new GoalPromotionFact(goal.Id.Value, new string('a', 40), new string('b', 40),
            new string('c', 40), clock.UtcNow, GoalPromotionFact.SoloProvenance);
        kernel.BindPostLandingPublication(goal.Id, publication);
        var duty = Assert.Single(goal.OutstandingPostLandingDuties);
        var observation = new CriterionPostLandingObservation(publication.PromotedMainRevision, scope, "runtime-1",
            "Operator verified activation", publication.PromotedAt.AddMinutes(1), publication.PromotedAt.AddMinutes(2),
            ActivationAttested: true, GoalId: goal.Id.Value, ObligationId: duty.Id);
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(CriterionEvidenceLifecycle.MatchesObservation(duty, observation, clock.UtcNow));

        var retired = goal.SupersedeCriterionEvidenceObligation(
            duty.Id, "operator", "Duty was overtaken", clock.UtcNow, lifecycleState: null);
        var error = Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, duty.Id,
            duty.Owner, publication.CandidateRevision, "would-reopen", duty.RequiredScope, true, "Observed", observation, "operator"));

        Assert.Contains("is Superseded", error.Message, StringComparison.Ordinal);
        Assert.Equal(publication, retired.Publication);
        Assert.False(retired.HasSatisfiedEvidenceFor(publication.CandidateRevision));
        Assert.Empty(goal.GetOutstandingCriterionEvidenceObligations(publication.CandidateRevision));
        Assert.Equal(retired, Assert.Single(goal.CriterionEvidenceObligations));
    }
}
