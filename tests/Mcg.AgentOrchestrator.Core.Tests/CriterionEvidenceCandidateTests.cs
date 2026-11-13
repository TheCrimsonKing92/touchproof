using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

public sealed class CriterionEvidenceCandidateTests
{
    [Xunit.Theory]
    [Xunit.InlineData(false, false)]
    [Xunit.InlineData(false, true)]
    [Xunit.InlineData(true, false)]
    [Xunit.InlineData(true, true)]
    public void CurrentCandidateRecordReplacesStaleOperatorEvidence(bool mapped, bool restart)
    {
        var (kernel, goal, obligation) = CreateOperatorObligation(mapped);
        var accepted = kernel.RecordCriterionEvidence(goal.Id, obligation.Id, obligation.Owner,
            "candidate-a", "receipt-a", obligation.RequiredScope, true, "A passed");

        kernel.RecordCriterionEvidence(goal.Id, obligation.Id, obligation.Owner,
            "candidate-b", "receipt-b", obligation.RequiredScope, true, "B passed");
        if (restart)
        {
            kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
            goal = kernel.GetGoal(goal.Id);
        }

        var current = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Equal(CriterionEvidenceState.Satisfied, current.State);
        Assert.Equal("candidate-b", current.ExpectedCandidateSha);
        Assert.Equal("candidate-b", current.CandidateSha);
        Assert.Equal("receipt-b", current.ReceiptId);
        Assert.Equal("B passed", current.Detail);
        Assert.True(current.HasSatisfiedEvidenceFor("candidate-b"));
        Assert.False(current.HasSatisfiedEvidenceFor("candidate-a"));
        Assert.Equal(new CriterionEvidenceReceipt(accepted.Owner, accepted.CandidateSha!, accepted.ReceiptId!,
            accepted.RequiredScope, true, accepted.Detail!, accepted.Provenance, accepted.RecordedAt),
            Assert.Single(current.PriorReceipts!));
    }

    [Xunit.Fact]
    public void ReplacementRetryAddsNoArchiveEntry()
    {
        var (kernel, goal, obligation) = CreateOperatorObligation(mapped: true);
        kernel.RecordCriterionEvidence(goal.Id, obligation.Id, obligation.Owner,
            "candidate-a", "receipt-a", obligation.RequiredScope, true, "A passed");
        var replaced = kernel.RecordCriterionEvidence(goal.Id, obligation.Id, obligation.Owner,
            "candidate-b", "receipt-b", obligation.RequiredScope, true, "B passed");
        var retry = kernel.RecordCriterionEvidence(goal.Id, obligation.Id, obligation.Owner,
            "candidate-b", "receipt-b", obligation.RequiredScope, true, "B passed");
        Assert.Equal(replaced, retry);
        Assert.Single(retry.PriorReceipts!);

        var next = kernel.RecordCriterionEvidence(goal.Id, obligation.Id, obligation.Owner,
            "candidate-c", "receipt-c", obligation.RequiredScope, true, "C passed");
        Assert.Equal(new[] { "receipt-a", "receipt-b" }, next.PriorReceipts!.Select(item => item.ReceiptId));
        Assert.Equal(next, kernel.RecordCriterionEvidence(goal.Id, obligation.Id, obligation.Owner,
            "candidate-a", "receipt-a", obligation.RequiredScope, true, "A passed"));
    }

    [Xunit.Theory]
    [Xunit.InlineData("owner")]
    [Xunit.InlineData("scope")]
    [Xunit.InlineData("receipt")]
    [Xunit.InlineData("receipt-conflict")]
    [Xunit.InlineData("failed")]
    [Xunit.InlineData("same-candidate-case")]
    [Xunit.InlineData("same-candidate-whitespace")]
    public void RejectedRecordKeepsSatisfiedOperatorEvidence(string rejection)
    {
        var (kernel, goal, obligation) = CreateOperatorObligation(mapped: rejection != "same-candidate-whitespace");
        var accepted = kernel.RecordCriterionEvidence(goal.Id, obligation.Id, obligation.Owner,
            "candidate-a", "receipt-a", obligation.RequiredScope, true, "A passed");
        var candidate = rejection switch
        {
            "receipt" => "candidate-a",
            "same-candidate-case" => "CANDIDATE-A",
            "same-candidate-whitespace" => " candidate-a ",
            _ => "candidate-b"
        };
        var error = Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, obligation.Id,
            rejection == "owner" ? CriterionEvidenceOwner.Acceptance : obligation.Owner, candidate,
            rejection == "receipt-conflict" ? "receipt-a" : "receipt-b",
            rejection == "scope" ? "wrong-scope" : obligation.RequiredScope, rejection != "failed", "B evidence"));

        var expected = rejection switch
        {
            "owner" or "scope" or "failed" => $"Evidence receipt is incompatible with obligation '{obligation.Id}'.",
            "receipt-conflict" => $"Receipt 'receipt-a' already identifies different evidence for obligation '{obligation.Id}'.",
            _ => $"Criterion obligation '{obligation.Id}' is already satisfied by an incompatible receipt."
        };
        Assert.Equal(expected, error.Message);
        Assert.Equal(accepted, Assert.Single(goal.CriterionEvidenceObligations));
    }

    [Xunit.Theory]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, false)]
    [Xunit.InlineData(CriterionEvidenceOwner.Operator, true)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, false)]
    [Xunit.InlineData(CriterionEvidenceOwner.Acceptance, true)]
    public void NewCandidateRequiresFreshEvidenceAndRetainsPreviousReceipts(CriterionEvidenceOwner owner, bool restart)
    {
        var (kernel, goal, mapped) = Create(owner);
        var accepted = kernel.RecordCriterionEvidence(goal.Id, mapped.Id, owner,
            "candidate-a", "receipt-a", mapped.RequiredScope, true, "A passed");
        Assert.Empty(goal.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        Assert.Single(goal.GetOutstandingCriterionEvidenceObligations("candidate-b"));
        Assert.Single(goal.GetOutstandingCriterionEvidenceObligations(null));

        var pending = kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, owner, "operator",
            mapped.RequiredScope, expectedCandidateSha: "candidate-b");
        Assert.Equal(CriterionEvidenceState.Pending, pending.State);
        Assert.Null(pending.CandidateSha);
        var previous = Assert.Single(pending.PriorReceipts!);
        Assert.Equal(accepted.CandidateSha, previous.CandidateSha);
        Assert.Equal(accepted.ReceiptId, previous.ReceiptId);
        Assert.True(previous.Passed);
        if (restart)
        {
            kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
                JsonSerializer.Serialize(kernel.ExportSnapshot()))!);
            goal = kernel.GetGoal(goal.Id);
        }
        Assert.Single(goal.OutstandingCriterionEvidenceObligations);
        Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, mapped.Id, owner,
            "candidate-a", "receipt-a", mapped.RequiredScope, true, "A passed"));
        kernel.RecordCriterionEvidence(goal.Id, mapped.Id, owner,
            "candidate-b", "receipt-b-red", mapped.RequiredScope, false, "B failed");
        Assert.Single(goal.GetOutstandingCriterionEvidenceObligations("candidate-b"));
        var completed = kernel.RecordCriterionEvidence(goal.Id, mapped.Id, owner,
            "candidate-b", "receipt-b-green", mapped.RequiredScope, true, "B passed after correction");

        Assert.Empty(goal.GetOutstandingCriterionEvidenceObligations("candidate-b"));
        Assert.Single(goal.GetOutstandingCriterionEvidenceObligations("candidate-a"));
        Assert.Collection(completed.PriorReceipts!,
            receipt => { Assert.Equal("receipt-a", receipt.ReceiptId); Assert.True(receipt.Passed); },
            receipt => { Assert.Equal("receipt-b-red", receipt.ReceiptId); Assert.False(receipt.Passed); });
    }

    [Xunit.Fact]
    public void RepeatingSatisfiedMappingIsIdempotent()
    {
        var (kernel, goal, mapped) = Create(CriterionEvidenceOwner.Operator);
        var accepted = kernel.RecordCriterionEvidence(goal.Id, mapped.Id, mapped.Owner,
            "candidate-a", "receipt-a", mapped.RequiredScope, true, "A passed");
        var repeated = kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, mapped.Owner, "operator",
            mapped.RequiredScope, expectedCandidateSha: "candidate-a");

        Assert.Equal(accepted, repeated);
        Assert.Null(repeated.PriorReceipts);
        Assert.Single(goal.CriterionEvidenceObligations);
    }

    [Xunit.Theory]
    [Xunit.InlineData(true, "Failed observation")]
    [Xunit.InlineData(false, "Different observation")]
    public void FailedReceiptIdentityCannotBeRewritten(bool passed, string detail)
    {
        var (kernel, goal, mapped) = Create(CriterionEvidenceOwner.Operator);
        var failed = kernel.RecordCriterionEvidence(goal.Id, mapped.Id, mapped.Owner,
            "candidate-a", "receipt-a", mapped.RequiredScope, false, "Failed observation");

        Assert.Throws<InvalidOperationException>(() => kernel.RecordCriterionEvidence(goal.Id, mapped.Id, mapped.Owner,
            "candidate-a", "receipt-a", mapped.RequiredScope, passed, detail));
        Assert.Equal(failed, Assert.Single(goal.CriterionEvidenceObligations));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, CriterionEvidenceObligation Obligation) CreateOperatorObligation(bool mapped)
    {
        if (mapped) return Create(CriterionEvidenceOwner.Operator);
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Replace operator evidence created by refinement");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Observe behavior", ["Operator observes"],
            VerificationClass.RealWorldDependent, [], []) { OperatorOwnedAcceptanceCriteria = ["Operator observes"] });
        var obligation = Assert.Single(goal.CriterionEvidenceObligations);
        Assert.Null(obligation.ExpectedCandidateSha);
        Assert.Equal(CriterionEvidenceOwner.Operator, obligation.Owner);
        return (kernel, goal, obligation);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, CriterionEvidenceObligation Mapped) Create(CriterionEvidenceOwner owner)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Require fresh candidate-bound evidence");
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Observe behavior", ["Behavior is proved"],
            VerificationClass.RealWorldDependent, [], []));
        var mapped = kernel.MapCriterionEvidenceOwner(goal.Id, 0, 1, owner, "operator",
            owner == CriterionEvidenceOwner.Acceptance ? CriterionEvidenceScopes.FullAcceptanceGate : "operator observation",
            expectedCandidateSha: "candidate-a");
        return (kernel, goal, mapped);
    }
}
