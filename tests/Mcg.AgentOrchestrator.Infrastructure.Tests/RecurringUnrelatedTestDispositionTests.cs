using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;
using static RecurringUnrelatedTestRuleTests;
using static ConductorDriverTests;

// Parallel-safe: fake stores, clocks and driver effects are owned by each test.
public sealed class RecurringUnrelatedTestDispositionTests
{
    internal sealed class Fixture
    {
        internal readonly RecurringTestFixItemFilerTests.FakeStore Store = new();
        internal readonly Dictionary<(GoalId, string), int> Counts = [];
        internal IReadOnlyList<RecurringTestPassedGate> Passes = [];
        internal int Failures;
        internal int Restores;
        internal int Escalations;
        internal int AttentionAttempts;
        internal bool FailAttention;
        internal bool LoseLedgerWrite;
        internal RecurringUnrelatedTestDisposition Handler()
        {
            var filer = new RecurringTestFixItemFiler(Store, (_, _) =>
            {
                AttentionAttempts++;
                if (FailAttention) throw new IOException("fake attention unavailable");
            }, _ => { });
            return new(_ => History, () => Passes, filer,
                (goal, sha) => Counts.GetValueOrDefault((goal, sha)), (goal, sha, _) =>
                {
                    if (LoseLedgerWrite) return false;
                    Counts[(goal, sha)] = Counts.GetValueOrDefault((goal, sha)) + 1;
                    return true;
                }, () => Now);
        }
        internal ConductorAdvanceResult? Run(Goal goal, string? candidate = "candidate") =>
            Handler().TryDispose(goal, goal.Id.Value[..8], ConductorAutonomyPolicy.Conservative,
                Summary(candidate), Reading(), (_, _) => Restores++,
                (_, _, _, _, _, _) => Failures++, (_, _, _) => Escalations++);
    }

    internal static AcceptanceVerificationSummary Summary(string? candidate = "candidate") =>
        new(false, [], FailedChecks: ["verifier lane"], BranchHeadSha: candidate, MainHeadSha: "main");

    [Fact(DisplayName = "The third recurring failure holds with a mapped operator and fix item")]
    public void ThirdFailureHoldsAndNewCandidateRestarts()
    {
        var fixture = new Fixture();
        var (_, goal) = SimpleGoal();
        for (var ordinal = 1; ordinal <= 2; ordinal++)
        {
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Run(goal)!.Outcome);
            Assert.Equal(RecurringUnrelatedTestDisposition.RegateKey, held.Decision!.DiscriminatingEvidence);
            Assert.Contains($"Re-gate {ordinal}/2", held.Reason, StringComparison.Ordinal);
            Assert.Equal(0, fixture.Failures);
        }
        var third = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Run(goal)!.Outcome);
        Assert.Equal(ConductorHoldOwner.Operator, third.Owner);
        Assert.Equal(RecurringUnrelatedTestDisposition.HoldKey, third.Decision!.DiscriminatingEvidence);
        Assert.Equal(("operator", "land-fix-item"), ConductorLandingBlockerRecorder.NextActions[third.Decision.DiscriminatingEvidence]);
        Assert.Contains(Assert.Single(fixture.Store.Items).Key, third.Reason, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Failures);
        Assert.Equal(1, fixture.Escalations);
        Assert.Equal(3, fixture.Restores);
        var next = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Run(goal, "new-candidate")!.Outcome);
        Assert.Contains("Re-gate 1/2", next.Reason, StringComparison.Ordinal);
    }

    [Theory(DisplayName = "A closed fix or three later passed gates disables recurring disposition")]
    [InlineData("closed")]
    [InlineData("passes")]
    public void ResetClearsDisposition(string reset)
    {
        var fixture = new Fixture();
        var (_, goal) = SimpleGoal();
        Assert.NotNull(fixture.Run(goal));
        if (reset == "closed")
        {
            var item = Assert.Single(fixture.Store.Items.Values);
            fixture.Store.Items[item.Id] = item with { Status = BacklogItemStatus.Done, UpdatedAt = Now.AddMinutes(-10) };
        }
        else fixture.Passes = [Pass(1), Pass(2), Pass(3)];
        Assert.Null(fixture.Run(goal));
        Assert.Single(fixture.Store.Items);
        Assert.Equal(1, fixture.Restores);
    }

    [Theory(DisplayName = "An unknown candidate or lost bound receipt holds without a worker retry")]
    [InlineData(true)]
    [InlineData(false)]
    public void MissingBoundEvidenceHolds(bool unknownCandidate)
    {
        var fixture = new Fixture { LoseLedgerWrite = !unknownCandidate };
        var (_, goal) = SimpleGoal();
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Run(goal, unknownCandidate ? null : "candidate")!.Outcome);
        Assert.Equal(ConductorHoldOwner.Operator, held.Owner);
        Assert.Equal(1, fixture.Failures);
        Assert.Empty(fixture.Counts);
    }

    [Fact(DisplayName = "Backlog failure preserves bounded unrelated re-gating and names the filing fault")]
    public void FilingFaultDoesNotRouteDeveloper()
    {
        var fixture = new Fixture();
        fixture.Store.FailWrites = true;
        var (_, goal) = SimpleGoal();
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Run(goal)!.Outcome);
        Assert.Contains("fix item not filed (IOException", held.Reason, StringComparison.Ordinal);
        Assert.Equal(RecurringUnrelatedTestDisposition.RegateKey, held.Decision!.DiscriminatingEvidence);
        Assert.Equal(0, fixture.Failures);
    }

    [Fact]
    public void AttentionFailureNamesOwnedItemAndRepairInDisposition()
    {
        var fixture = new Fixture { FailAttention = true };
        var (_, goal) = SimpleGoal();
        var held = Assert.IsType<ConductorAdvanceOutcome.Held>(fixture.Run(goal)!.Outcome);
        var id = Assert.Single(fixture.Store.Items.Values).Id;
        Assert.Contains(id, held.Reason, StringComparison.Ordinal);
        Assert.Contains("attention not raised: IOException: fake attention unavailable", held.Reason, StringComparison.Ordinal);
        Assert.Contains("next action: raise attention for this existing backlog item", held.Reason, StringComparison.Ordinal);
        Assert.Contains(id, Assert.Single(fixture.Store.Notes), StringComparison.Ordinal);
        Assert.Equal(RecurringUnrelatedTestDisposition.RegateKey, held.Decision!.DiscriminatingEvidence);
        Assert.Equal(0, fixture.Failures);
        Assert.NotNull(fixture.Run(goal));
        Assert.Equal(1, fixture.AttentionAttempts);
        Assert.Single(fixture.Store.Items);
    }

    [Fact]
    public void CarryForwardDoesNotClearRecurrenceBeforeThreeExecutedGates()
    {
        var root = Path.Combine(Path.GetTempPath(), "recurrence-passes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var fixture = new Fixture();
            var (_, goal) = SimpleGoal();
            GoalOperationJournal.AcceptanceGatePassed(root, goal, "conductor:acceptance", "candidate-a", "main-a",
                "Executed gate", Now.AddMinutes(-30));
            GoalOperationJournal.AcceptancePassed(root, goal, ConductorDriver.AcceptanceMainAdvanceCarryOperation,
                "candidate-a", "main-b", "Carried gate evidence", Now.AddMinutes(-20));
            GoalOperationJournal.AcceptancePassed(root, goal, "acceptance", "candidate-b", "main-b",
                "Executed gate", Now.AddMinutes(-10));

            fixture.Passes = RecurringUnrelatedTestDisposition.ReadPassedGates(root);
            Assert.Equal(2, fixture.Passes.Count);
            Assert.NotNull(fixture.Run(goal));

            GoalOperationJournal.AcceptanceGatePassed(root, goal, "acceptance", "candidate-c", "main-b",
                "Executed gate", Now.AddMinutes(-5));
            fixture.Passes = RecurringUnrelatedTestDisposition.ReadPassedGates(root);
            Assert.Equal(3, fixture.Passes.Count);
            Assert.Null(fixture.Run(goal));
            Assert.Equal(1, fixture.Restores);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact(DisplayName = "The durable re-gate bound survives a new reader and stays scoped to a candidate")]
    public void LedgerPersistsPerCandidate()
    {
        var root = Path.Combine(Path.GetTempPath(), "recurrence-ledger-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "index.jsonl");
            var goal = GoalId.New();
            Assert.True(new RecurringUnrelatedRegateLedger(new(path)).Append(goal, "candidate", Now));
            var reopened = new RecurringUnrelatedRegateLedger(new(path));
            Assert.Equal(1, reopened.Count(goal, "CANDIDATE"));
            Assert.Equal(0, reopened.Count(goal, "other-candidate"));
            Assert.Equal(0, reopened.Count(GoalId.New(), "candidate"));
            Assert.True(reopened.Append(goal, "candidate", Now.AddDays(30)));
            Assert.Equal(2, new RecurringUnrelatedRegateLedger(new(path)).Count(goal, "candidate"));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
