using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: in-memory kernels and immutable records only.
public sealed class CriterionEvidenceRephaseTests
{
    private static readonly CriterionObservationScope Scope =
        new(CriterionObservationKind.Runtime, "Operator checks activation", "promoted service");

    [Theory]
    [InlineData(CriterionObservationKind.Runtime)]
    [InlineData(CriterionObservationKind.RepositoryState)]
    public void Apply_EligibleObligation_PreservesFieldsAndAppendsAttribution(CriterionObservationKind kind)
    {
        var (_, goal) = CreateGoal();
        var before = Assert.Single(goal.CriterionEvidenceObligations);
        var time = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var scope = Scope with { Kind = kind };

        var result = CriterionEvidenceRephase.Apply(before, scope, "operator\nSmith", "check\nlater", time);

        Assert.Equal(CriterionEvidencePhase.PostLanding, result.Phase);
        Assert.Equal(scope, result.ObservationScope);
        Assert.Equal($"{before.Provenance}; re-phased to PostLanding by operator Smith at {time:u}; reason=check later", result.Provenance);
        Assert.Equal(before, result with
        {
            Phase = before.Phase, ObservationScope = before.ObservationScope, Provenance = before.Provenance
        });
        Assert.True(result.HasValidEvidenceState);
    }

    [Fact]
    public void Rephase_CriterionSeven_RemovesLandingPrerequisiteAcrossRestore()
    {
        var (kernel, goal) = CreateGoal();
        var before = Assert.Single(goal.LandingPrerequisiteObligations);
        Assert.Equal("criterion-v1-6", before.Id);
        Assert.Equal(CriterionEvidenceState.Pending, before.State);
        Assert.Equal(CriterionEvidenceOwner.Operator, before.Owner);
        Assert.Equal(CriterionEvidencePhase.PreLanding, before.Phase);
        Assert.Single(goal.GetLandingPrerequisiteObligations("passing-candidate"));

        var result = kernel.RephaseCriterionEvidenceObligation(goal.Id, before.Id, Scope, "operator", "check after landing");

        Assert.Empty(goal.LandingPrerequisiteObligations);
        Assert.Empty(goal.GetLandingPrerequisiteObligations("passing-candidate"));
        Assert.Equal(result, Assert.Single(goal.OutstandingPostLandingDuties));
        var restored = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(
            JsonSerializer.Serialize(kernel.ExportSnapshot()))!).GetGoal(goal.Id);
        Assert.Equal(JsonSerializer.Serialize(result), JsonSerializer.Serialize(Assert.Single(restored.CriterionEvidenceObligations)));
        Assert.Empty(restored.LandingPrerequisiteObligations);
        Assert.Empty(restored.GetLandingPrerequisiteObligations("passing-candidate"));
        Assert.Single(restored.OutstandingPostLandingDuties);
    }

    [Theory]
    [InlineData("Satisfied", "Satisfied")]
    [InlineData("Failed", "Failed")]
    [InlineData("Repaired", "Repaired")]
    [InlineData("Acceptance", "Acceptance")]
    [InlineData("Unknown", "Unknown")]
    [InlineData("PostLanding", "PostLanding")]
    [InlineData("missing-scope", "observation scope")]
    [InlineData("blank-condition", "observation scope")]
    [InlineData("blank-target", "observation scope")]
    [InlineData("undefined-kind", "observation scope")]
    [InlineData("blank-reason", "reason")]
    [InlineData("blank-actor", "actor")]
    [InlineData("prior-receipt", "prior receipts")]
    public void Rephase_IneligibleRequest_RefusesWithoutChangingStoredObligation(string shape, string message)
    {
        var (kernel, goal) = CreateGoal();
        var id = Assert.Single(goal.CriterionEvidenceObligations).Id;
        if (shape is "Satisfied" or "Failed" or "prior-receipt")
        {
            kernel.RecordCriterionEvidence(goal.Id, id, CriterionEvidenceOwner.Operator,
                "candidate", "receipt", "operator observation", shape != "Failed", "Checked");
            if (shape == "prior-receipt")
                kernel.MapCriterionEvidenceOwner(goal.Id, 6, 1, CriterionEvidenceOwner.Operator, "operator",
                    expectedCandidateSha: "next-candidate");
        }
        if (shape == "Acceptance")
            kernel.MapCriterionEvidenceOwner(goal.Id, 6, 1, CriterionEvidenceOwner.Acceptance,
                "operator", CriterionEvidenceScopes.FullAcceptanceGate, "gate", "candidate");
        if (shape is "Unknown" or "Repaired")
        {
            var malformed = new CriterionEvidenceObligation("malformed", 99, 99, "corrupt",
                CriterionEvidenceOwner.Unknown, CriterionEvidenceState.Pending, "mapping needed",
                "malformed persisted obligation", DateTimeOffset.UnixEpoch);
            goal.RestoreCriterionEvidenceObligations([.. goal.CriterionEvidenceObligations, malformed]);
            id = goal.CriterionEvidenceObligations.Single(item => item.Owner == CriterionEvidenceOwner.Unknown).Id;
            if (shape == "Repaired")
                kernel.RepairMalformedCriterionEvidenceObligation(goal.Id, id, 6, 1,
                    CriterionEvidenceOwner.Operator, "operator", "restore authority", expectedCandidateSha: "candidate");
        }
        if (shape == "PostLanding")
            kernel.RephaseCriterionEvidenceObligation(goal.Id, id, Scope, "operator", "check later");
        var obligation = goal.CriterionEvidenceObligations.Single(item => item.Id == id);
        Assert.Equal(shape switch
        {
            "Satisfied" => CriterionEvidenceState.Satisfied,
            "Failed" => CriterionEvidenceState.Failed,
            "Repaired" => CriterionEvidenceState.Repaired,
            _ => CriterionEvidenceState.Pending
        }, obligation.State);
        Assert.Equal(shape switch
        {
            "Acceptance" => CriterionEvidenceOwner.Acceptance,
            "Unknown" or "Repaired" => CriterionEvidenceOwner.Unknown,
            _ => CriterionEvidenceOwner.Operator
        }, obligation.Owner);
        Assert.Equal(shape == "PostLanding" ? CriterionEvidencePhase.PostLanding : CriterionEvidencePhase.PreLanding,
            obligation.Phase);
        if (shape == "prior-receipt") Assert.Single(obligation.PriorReceipts!);
        CriterionObservationScope? scope = shape switch
        {
            "missing-scope" => null,
            "blank-condition" => Scope with { Condition = " " },
            "blank-target" => Scope with { Target = " " },
            "undefined-kind" => Scope with { Kind = (CriterionObservationKind)99 },
            _ => Scope
        };
        var before = JsonSerializer.Serialize(goal.CriterionEvidenceObligations);

        var error = Assert.ThrowsAny<Exception>(() => kernel.RephaseCriterionEvidenceObligation(
            goal.Id, id, scope, shape == "blank-actor" ? " " : "operator", shape == "blank-reason" ? " " : "later"));

        Assert.Contains(id, error.Message, StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(goal.CriterionEvidenceObligations));
    }

    [Fact]
    public void Apply_WorkerOwned_RefusesWithoutChangingRecord()
    {
        var (_, goal) = CreateGoal();
        var obligation = Assert.Single(goal.CriterionEvidenceObligations) with { Owner = CriterionEvidenceOwner.Worker };
        var before = JsonSerializer.Serialize(obligation);
        var error = Assert.Throws<InvalidOperationException>(() =>
            CriterionEvidenceRephase.Apply(obligation, Scope, "operator", "later", DateTimeOffset.UnixEpoch));
        Assert.Contains("Worker", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, JsonSerializer.Serialize(obligation));
    }

    [Fact]
    public void Rephase_UnknownId_RefusesWithoutChangingObligations()
    {
        var (kernel, goal) = CreateGoal();
        var before = JsonSerializer.Serialize(goal.CriterionEvidenceObligations);
        var error = Assert.Throws<KeyNotFoundException>(() =>
            kernel.RephaseCriterionEvidenceObligation(goal.Id, "missing", Scope, "operator", "later"));
        Assert.Equal("Criterion evidence obligation 'missing' was not found.", error.Message);
        Assert.Equal(before, JsonSerializer.Serialize(goal.CriterionEvidenceObligations));
    }

    [Fact]
    public void Restore_UnattributedPhaseChange_RetainsLandingHold()
    {
        var (_, goal) = CreateGoal();
        var obligation = Assert.Single(goal.CriterionEvidenceObligations);
        goal.RestoreCriterionEvidenceObligations([obligation with
        {
            Phase = CriterionEvidencePhase.PostLanding, ObservationScope = Scope
        }]);
        Assert.NotEmpty(goal.LandingPrerequisiteObligations);
        Assert.Equal(CriterionEvidenceOwner.Unknown, Assert.Single(goal.CriterionEvidenceObligations).Owner);
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal) CreateGoal()
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("Re-phase criterion seven");
        var criteria = Enumerable.Range(1, 7).Select(number => $"Criterion {number}").ToArray();
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Seven criteria", criteria,
            VerificationClass.TestVerifiable, [], []) { OperatorOwnedAcceptanceCriteria = [criteria[6]] });
        return (kernel, goal);
    }
}
