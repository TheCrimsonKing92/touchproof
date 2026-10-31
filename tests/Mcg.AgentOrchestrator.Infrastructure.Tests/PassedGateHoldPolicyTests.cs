using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: per-test kernels and a uniquely owned SQLite directory; no shared providers or processes.
public sealed class PassedGateHoldPolicyTests
{
    private const string Candidate = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Main = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Wait = "cccccccccccccccccccccccccccccccc";

    [Theory]
    [InlineData("landing/landing-held-after-pass", "gate test obligation 12345678 (brief v2) selector-not-found: MissingTests; source report-1; closest: SkippedTests", "gate test obligation", "reopen-regate")]
    [InlineData("landing/criterion-evidence-rebind-outstanding", "criterion 2:Operator:Pending:next=owner-receipt", "criterion evidence obligation", "criterion-evidence-record")]
    [InlineData("untyped/None", "landing-denylist owner approval required: rule=policy path=config/policy.json", "landing denylist", "approve-policy-change")]
    [InlineData("landing/landing-held-after-pass", "goal g has pending human input: request " + Wait + " (OwnerReview, owner operator)", "pending human wait", "attention answer")]
    public void Current_passed_receipt_names_category_rule_evidence_and_command(
        string kind, string detail, string category, string verb)
    {
        var goal = Goal(kind, detail);
        var hold = PassedGateRegateAdmission.Resolve(goal, Receipt(goal), Candidate);
        Assert.NotNull(hold);
        Assert.Equal(category, hold.CategoryText);
        Assert.NotEmpty(hold.Rule);
        Assert.Contains(detail, hold.Evidence);
        Assert.Contains(verb, string.Join("\n", hold.ResolvingVerbs));
        Assert.All(hold.ResolvingVerbs, command => Assert.Contains(goal.Id.Value, command));
        if (hold.Category is PassedGateHoldCategory.GateTestObligation or PassedGateHoldCategory.CriterionEvidenceObligation)
        {
            Assert.Contains("retry", hold.ResolvingVerbs[1]);
            Assert.Contains("goal-amend", hold.ResolvingVerbs[2]);
        }
        Assert.Equal("passed-attempt", hold.AttemptId);
        Assert.Equal(Candidate, hold.Candidate);
        Assert.Equal("passed.result.json", hold.ReceiptPath);
        if (category == "pending human wait") Assert.Contains(Wait, hold.ResolvingVerbs.Single());
    }

    [Fact]
    public void Pending_wait_precedes_test_obligation_and_preserves_other_blockers()
    {
        const string other = "gate test obligation 12345678 (brief v2) failed: Tests; source failed-receipt";
        var goal = Goal("landing/landing-held-after-pass",
            "acceptance passed at aaaaaaaa; 1 pending human wait(s) (OwnerReview cccccccc); " + other);
        var hold = PassedGateRegateAdmission.Resolve(goal, Receipt(goal), Candidate)!;
        Assert.Equal("pending human wait", hold.CategoryText);
        Assert.Contains(other, hold.Evidence);
        Assert.Contains("cccccccc", hold.ResolvingVerbs.Single());
    }

    [Fact]
    public void Superseded_invalidated_failed_missing_and_different_candidate_receipts_do_not_admit_regate()
    {
        var goal = Goal("landing/criterion-evidence-rebind-outstanding", "criterion 2 missing evidence");
        var receipt = Receipt(goal);
        Assert.NotNull(PassedGateRegateAdmission.Resolve(goal, receipt, Candidate));
        foreach (var rejected in new[]
        {
            receipt with { Outcome = ConductorParallelAcceptanceAttemptOutcome.Running },
            receipt with { Outcome = ConductorParallelAcceptanceAttemptOutcome.Failed },
            receipt with { GoalId = "different-goal" },
            receipt with { SupersededBy = "successor" },
            receipt with { ReconciledAt = null },
            receipt with { ReplayLandedAt = DateTimeOffset.UnixEpoch },
            receipt with { RegateRequestedAt = DateTimeOffset.UnixEpoch }
        }) Assert.Null(PassedGateRegateAdmission.Resolve(goal, rejected, Candidate));
        Assert.Null(PassedGateRegateAdmission.Resolve(goal, null, Candidate));
        Assert.Null(PassedGateRegateAdmission.Resolve(goal, receipt, Main));
    }

    [Fact]
    public async Task Decision_and_attention_refresh_one_item_and_clear_when_hold_clears()
    {
        Assert.True(OwnerQuestionViewOnly.IsViewOnly(OwnerQuestionKind.PassedGateHold));
        var root = Path.Combine(Path.GetTempPath(), nameof(PassedGateHoldPolicyTests), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new CollaborationItemStore(Path.Combine(root, "collaboration-items.db"));
            var goal = Goal("landing/criterion-evidence-rebind-outstanding", "criterion 2 missing evidence");
            var hold = PassedGateRegateAdmission.Resolve(goal, Receipt(goal), Candidate)!;
            PassedGateHoldAttention.Reconcile(store, goal, hold);
            PassedGateHoldAttention.Reconcile(store, goal, hold);
            var first = Assert.Single(await store.GetAttentionQueueAsync());
            Assert.Equal(CollaborationItemType.Decision, first.Type);
            Assert.Equal(PassedGateHoldAttention.Prefix + goal.Id.Value, first.CorrelationKey);
            var changed = hold with { Category = PassedGateHoldCategory.LandingDenylist, Rule = "rule=policy" };
            PassedGateHoldAttention.Reconcile(store, goal, changed);
            var second = Assert.Single(await store.GetAttentionQueueAsync());
            Assert.Equal(first.Id, second.Id);
            Assert.Contains("landing denylist", second.Body);
            Assert.Contains("rule=policy", second.Body);
            PassedGateHoldAttention.Reconcile(store, goal, null);
            Assert.Empty(await store.GetAttentionQueueAsync());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Goal Goal(string kind, string detail)
    {
        var kernel = new AgentOrchestratorKernel();
        var goal = kernel.CreateGoal("passed gate landing hold");
        var snapshot = kernel.ExportSnapshot();
        kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(item => item with
            {
                Status = GoalStatus.Verified,
                LandingBlocker = new(kind, detail, "hold", DateTimeOffset.UnixEpoch, "operator", "resolve")
            }).ToArray()
        });
        return kernel.GetGoal(goal.Id);
    }

    private static ConductorParallelAcceptanceAttempt Receipt(Goal goal) => new(
        "passed-attempt", goal.Id.Value, goal.Id.Value[..8], 0, Candidate, Main,
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, 0,
        ConductorParallelAcceptanceAttemptOutcome.Passed, "", "", "", "", "passed.result.json", "passed.attempt.json",
        ReconciledAt: DateTimeOffset.UnixEpoch);
}
