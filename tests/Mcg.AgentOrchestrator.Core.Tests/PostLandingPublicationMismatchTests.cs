using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all lifecycle state and the clock are private to each case.
public sealed class PostLandingPublicationMismatchTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Bind_MixedCandidates_PreservesOpenDutyAndBindsMatchingDuties(bool reload)
    {
        var clock = new FakeClock();
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal("Keep mismatched publication visible");
        string[] criteria = ["Observe original candidate", "Observe landed candidate", "Observe any candidate"];
        var scope = new CriterionObservationScope(CriterionObservationKind.Runtime, "Observe activated behavior", "runtime");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Observe", criteria,
            VerificationClass.RealWorldDependent, [], [])
        {
            OperatorOwnedAcceptanceCriteria = criteria,
            CriterionEvidenceDeclarations = criteria.Select(criterion =>
                new CriterionEvidenceDeclaration(criterion, CriterionEvidencePhase.PostLanding, scope, Atomic: true)).ToArray()
        });
        var original = new string('a', 40);
        var landed = new string('b', 40);
        // Reproduce an already-persisted candidate declaration, as in landing recovery.
        var snapshot = kernel.ExportGoalSnapshot(goal.Id);
        kernel.ReplaceGoalWithSnapshot(snapshot with
        {
            CriterionEvidenceObligations = snapshot.CriterionEvidenceObligations!.Select(duty => duty with
            {
                ExpectedCandidateSha = duty.CriterionIndex switch { 0 => original, 1 => landed.ToUpperInvariant(), _ => null }
            }).ToArray()
        });
        goal = kernel.GetGoal(goal.Id);
        var publication = new GoalPromotionFact(goal.Id.Value, landed, new string('c', 40),
            new string('d', 40), clock.UtcNow, GoalPromotionFact.SoloProvenance);

        kernel.BindPostLandingPublication(goal.Id, publication);
        if (reload)
        {
            kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
            goal = kernel.GetGoal(goal.Id);
        }

        var open = Assert.Single(goal.UnpublishedPostLandingDuties);
        Assert.Equal(original, open.ExpectedCandidateSha);
        Assert.Equal(CriterionEvidenceState.Pending, open.State);
        Assert.Null(open.Publication);
        Assert.All(goal.CriterionEvidenceObligations.Where(duty => duty.Id != open.Id), duty =>
        {
            Assert.Equal(publication, duty.Publication);
            Assert.Equal(landed, duty.ExpectedCandidateSha);
        });
        var description = PostLandingOperatorCheck.DescribeOpen([], goal.UnpublishedPostLandingDuties);
        Assert.Contains(open.Id, description, StringComparison.Ordinal);
        Assert.Contains($"criterion {open.CriterionNumber}", description, StringComparison.Ordinal);
        Assert.Contains(original[..8], description, StringComparison.Ordinal);
        Assert.Equal(PostLandingOperatorCheck.DescribeOpen([]), PostLandingOperatorCheck.DescribeOpen([], []));
        var request = kernel.RequestHumanInputDeduplicated(goal.Id, null, "Observe the runtime",
            kind: HumanWaitKind.ProspectiveAcceptanceEvidence, evidenceOwner: "operator",
            plannerEvidence: new PlannerEvidencePayload(1, "runtime-check", "post-landing", "operator",
                "Observe the runtime", "Requires activation", null, null, null)).Request;
        Assert.NotNull(PostLandingOperatorCheck.DescribeOpen([request]));
        Assert.Equal(PostLandingOperatorCheck.DescribeOpen([request]), PostLandingOperatorCheck.DescribeOpen([request], []));
        Assert.Equal(PostLandingOperatorCheck.DescribeOpen([request]) + "; " + description!["open post-landing operator check(s): ".Length..],
            PostLandingOperatorCheck.DescribeOpen([request], goal.UnpublishedPostLandingDuties));
    }
}
