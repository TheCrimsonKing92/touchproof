using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.Core;
using Xunit;

// Parallel-safe: pure candidate/obligation planning, no process or shared state.
public sealed class GoalBranchCommitRebindPlanTests
{
    private const string OldHead = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string NewHead = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly GoalId GoalId = new("12345678901234567890123456789012");

    [Fact]
    public void Create_MixedObligations_SelectsOnlyCurrentAcceptanceBindingsToFullPreviousHead()
    {
        var eligible = Duty(0);
        var second = Duty(1) with { ExpectedCandidateSha = OldHead.ToUpperInvariant() };
        var duties = new[]
        {
            eligible, second,
            Duty(2) with { ExpectedCandidateSha = null },
            Duty(3) with { Owner = CriterionEvidenceOwner.Operator },
            Duty(4) with { ExpectedCandidateSha = "cccccccccccccccccccccccccccccccccccccccc" },
            Duty(5) with { ExpectedCandidateSha = OldHead[..8] },
            Duty(6) with { Phase = CriterionEvidencePhase.PostLanding },
            Duty(7) with { State = CriterionEvidenceState.Repaired },
            Duty(8) with { CriterionVersion = 1 },
            Duty(9) with { Owner = CriterionEvidenceOwner.Unknown },
            Duty(10) with { ExpectedCandidateSha = NewHead }
        };

        var plan = GoalBranchCommitRebindPlan.Create(duties, 2, OldHead, NewHead);

        Assert.Equal(new[] { eligible, second }, plan.Eligible);
        Assert.Equal(9, plan.Unchanged.Count);
        Assert.Equal("unbound", plan.Unchanged[duties[2].Id]);
        Assert.Equal("operator-owned", plan.Unchanged[duties[3].Id]);
        Assert.Equal("bound-to-other-sha", plan.Unchanged[duties[4].Id]);
        Assert.Equal("bound-to-other-sha", plan.Unchanged[duties[5].Id]);
        Assert.Equal("post-landing", plan.Unchanged[duties[6].Id]);
        Assert.Equal("repaired", plan.Unchanged[duties[7].Id]);
        Assert.Equal("historical-version", plan.Unchanged[duties[8].Id]);
        Assert.Equal("not-acceptance-owned", plan.Unchanged[duties[9].Id]);
        Assert.Equal("already-bound-to-new-head", plan.Unchanged[duties[10].Id]);
        Assert.Null(duties[2].ExpectedCandidateSha);
        Assert.Equal(CriterionEvidenceOwner.Operator, duties[3].Owner);
    }

    [Fact]
    public void Mapping_EligibleObligation_UsesScriptRouteAndZeroBasedFindingIdentity()
    {
        var duty = Duty(3) with { RequiredScope = "prior-route", FindingStableId = "prior-finding" };

        var mapping = GoalBranchCommitRebindPlan.Mapping(GoalId, NewHead, duty);

        Assert.Equal(new CriterionEvidenceMappingOperatorIntentPayload(3, 2, CriterionEvidenceOwner.Acceptance,
            "acceptance:full-gate", "rebind-12345678-criterion-3", NewHead), mapping);
        Assert.Equal("prior-route", duty.RequiredScope);
        Assert.Equal(OldHead, duty.ExpectedCandidateSha);
    }

    [Fact]
    public void RebindKey_RetryAndDifferentTransitions_StableForRetryAndDistinctForEveryIdentityAxis()
    {
        var duty = Duty(3);
        var key = GoalBranchCommitRebindPlan.RebindKey(GoalId, NewHead, duty);

        Assert.Equal($"goal-branch-commit-rebind:{GoalId.Value}:{NewHead}:4:v2", key);
        Assert.Equal(key, GoalBranchCommitRebindPlan.RebindKey(GoalId, NewHead, duty));
        Assert.NotEqual(key, GoalBranchCommitRebindPlan.RebindKey(new GoalId("12345678901234567890123456789013"), NewHead, duty));
        Assert.NotEqual(key, GoalBranchCommitRebindPlan.RebindKey(GoalId, OldHead, duty));
        Assert.NotEqual(key, GoalBranchCommitRebindPlan.RebindKey(GoalId, NewHead, duty with { CriterionIndex = 4 }));
        Assert.NotEqual(key, GoalBranchCommitRebindPlan.RebindKey(GoalId, NewHead, duty with { CriterionVersion = 3 }));
        Assert.Equal(GoalBranchCommitRebindPlan.StableId(GoalId, duty),
            GoalBranchCommitRebindPlan.Mapping(GoalId, OldHead, duty).FindingStableId);
    }

    private static CriterionEvidenceObligation Duty(int index) => new(
        CriterionEvidenceObligation.BuildId(2, index), index, 2, $"Requirement {index + 1}",
        CriterionEvidenceOwner.Acceptance, CriterionEvidenceState.Pending, CriterionEvidenceScopes.FullAcceptanceGate,
        "test", DateTimeOffset.UnixEpoch, ExpectedCandidateSha: OldHead);
}
