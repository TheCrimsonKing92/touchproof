using Mcg.AgentOrchestrator.Core;

// Parallel-safe: every case owns its kernel and all evidence.
public sealed class PlannerBriefAmendmentOwnershipTests
{
    private const string Evidence = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"execution","availability":"post-implementation","owner":"acceptance-gate","needed":"Run the regression","reason":"Candidate must exist first","tests":["PlannerBriefAmendmentOwnershipTests"]}
        """;
    private const string Rejection = "Planner ownership requires a valid structured directive within the authoritative criteria.";

    [Fact]
    public void MixedBatch_AmendmentDoesNotInvalidateEvidenceOwnership()
    {
        var (kernel, goal, task) = Create();
        var evidence = AgentOutputDirectives.ParseHumanInputRequest(Evidence, AgentRole.Planner).Directive!;
        var batch = Batch(evidence, amendment: true);

        Assert.Null(PlannerEvidenceRecorder.PlanOwnership(goal, task, batch, goal.AuthoritativeBrief.Version,
            DateTimeOffset.UtcNow, out var changes));

        var source = Assert.Single(Assert.Single(changes).PlannerSources!);
        Assert.Equal(new PlannerEvidenceIdentity(goal.Id.Value, task.Id.Value, goal.AuthoritativeBrief.Version, 1, "execution"), source.Identity);
        Assert.Equal(evidence.PlannerEvidence, source.Directive);
        Assert.Null(kernel.RecordPlannerEvidenceBatch(goal, task, batch, goal.AuthoritativeBrief.Version, out var blocked));
        Assert.True(blocked);
        Assert.Equal(source.Identity, Assert.Single(goal.GateTestObligations).PlannerIdentity);
        Assert.Equal(HumanWaitKind.PlannerBriefAmendment, Assert.Single(kernel.GetPendingHumanInput(goal.Id)).Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OutOfRangeEvidence_WithOrWithoutAmendmentRetainsRejection(bool amendment)
    {
        var (kernel, goal, task) = Create();
        var evidence = AgentOutputDirectives.ParseHumanInputRequest(Evidence.Replace("criterion_index\":1", "criterion_index\":2"), AgentRole.Planner).Directive!;
        var batch = Batch(evidence, amendment);

        Assert.Equal(Rejection, PlannerEvidenceRecorder.PlanOwnership(goal, task, batch,
            goal.AuthoritativeBrief.Version, DateTimeOffset.UtcNow, out var changes));
        Assert.Empty(changes);
        Assert.Equal("Planner evidence criterion 2 is outside the authoritative criteria; replan.",
            kernel.RecordPlannerEvidenceBatch(goal, task, batch, goal.AuthoritativeBrief.Version, out _));
        Assert.Empty(goal.GateTestObligations);
    }

    [Fact]
    public void MissingEvidencePayload_IsRejectedEvenWhenQuestionLooksLikeAmendment()
    {
        var (_, goal, task) = Create();
        var evidence = new HumanInputDirective("Planner brief amendment requested for criterion 1.", "fingerprint",
            Kind: HumanWaitKind.ProspectiveAcceptanceEvidence);
        Assert.Equal(Rejection, PlannerEvidenceRecorder.PlanOwnership(goal, task, Batch(evidence, true),
            goal.AuthoritativeBrief.Version, DateTimeOffset.UtcNow, out var changes));
        Assert.Empty(changes);
    }

    [Fact]
    public void AmendmentAlone_ContributesNoOwnershipOrCoverage()
    {
        var (_, goal, task) = Create();
        var batch = new HumanInputDirectiveParseResult(Amendment(), null);
        Assert.Null(PlannerEvidenceRecorder.PlanOwnership(goal, task, batch, goal.AuthoritativeBrief.Version,
            DateTimeOffset.UtcNow, out var changes));
        Assert.Empty(changes);
        Assert.Empty(goal.CriterionEvidenceObligations);
    }

    private static HumanInputDirective Amendment() => AgentOutputDirectives.ParseHumanInputRequest(
        """
        PLANNER_BRIEF_AMENDMENT: {"criterion_index":1,"defect":"Conflicts with regression.","evidence":"Existing.cs:1","proposed_text":"Preserve behavior."}
        """, AgentRole.Planner).Directive! with { Question = "Unrelated wording" };

    private static HumanInputDirectiveParseResult Batch(HumanInputDirective evidence, bool amendment) =>
        new(null, null) { Directives = amendment ? [Amendment(), evidence] : [evidence], EvidenceLineCount = 1 };

    private static (AgentOrchestratorKernel, Goal, TaskSpec) Create()
    {
        var kernel = new AgentOrchestratorKernel();
        var task = new TaskSpec(TaskId.New(), "Plan", AgentRole.Planner);
        var goal = kernel.CreateGoal("Separate amendment requests from evidence ownership", [task]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Preserve behavior", ["Run the regression."],
            VerificationClass.TestVerifiable, [], []));
        return (kernel, goal, task);
    }
}
