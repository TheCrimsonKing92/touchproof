using System.Drawing;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using TestGoal = OwnerConsoleGoalChangeSummaryTestGoal;
using Queries = OwnerConsoleGoalChangeSummaryQueries;
using EmptyTail = OwnerConsoleGoalChangeSummaryEmptyTail;
using static OwnerConsoleGoalChangeSummaryFixtures;
using static OwnerConsoleGoalChangeSummaryRendering;
using static OwnerConsoleGoalChangeSummaryAcceptanceJournal;

// Parallel-safe: instance-owned snapshots, I/O delegates, event gates and uninitialized GUI views.
public sealed class OwnerConsoleGoalChangeSummaryDispatchFailureAndPlanTests
{
    [Theory]
    [InlineData(AgentRole.Tester, false)]
    [InlineData(AgentRole.Reviewer, false)]
    [InlineData(AgentRole.Tester, true)]
    [InlineData(AgentRole.Reviewer, true)]
    public async Task ReadOnlyDispatch_PreservesOnlyMatchingContributingRange(AgentRole role, bool advanced)
    {
        var original = Fixture("changed").ToSnapshot();
        var candidate = advanced ? Next : Candidate;
        var readOnly = original.Tasks.Single(task => task.RequiredRole == AgentRole.Developer) with
        {
            Id = TaskId.New().Value, RequiredRole = role, Status = WorkTaskStatus.Completed,
            LastVerification = null, VerificationHistory = null, DispatchHistory = null,
            LastDispatch = new("reader", "reader", "read-only-artifact", At.AddMinutes(2),
                BaseCommit: candidate, ResultCommit: candidate)
        };
        var goal = new TestGoal(original with { Tasks = [.. original.Tasks, readOnly] });
        var reads = new List<(string Base, string Candidate)>();
        var query = new OwnerGoalChangeSummaryQuery(changes: (basis, head, _) =>
        {
            reads.Add((basis, head));
            // Model the real empty result for HEAD..HEAD; the pre-fix selection loses the file.
            return Task.FromResult(basis == head ? new CommittedGoalChanges(true, [], 0) :
                new CommittedGoalChanges(true, ["M src/Feature.cs"], 1));
        }, mergeBase: (_, _) => Task.FromResult<string?>(Basis));
        var (preview, detail) = await RenderSelectedGoalAsync(goal, query);
        Assert.Equal(advanced ? new[] { (Next, candidate), (Basis, candidate) } : new[] { (Basis, candidate) }, reads);
        Assert.Contains("Candidate: " + candidate, detail);
        Assert.Contains("Git " + (advanced ? Next : Basis) + ".." + candidate, detail);
        var expected = advanced ? "Changed: observed empty committed range" : "Changed: observed 1 files";
        Assert.Contains("Changes 1 file in src: Feature.cs", preview);
        Assert.Contains(expected, detail);
        if (advanced) Assert.DoesNotContain("M src/Feature.cs", detail);
        else
        {
            Assert.Contains("Feature.cs", preview);
            Assert.Contains("M src/Feature.cs", detail);
            Assert.Contains("latest contributing dispatch range, not whole-goal coverage", detail);
        }
    }

    [Theory]
    [InlineData("current", "failed")]
    [InlineData("stale-main", "pending")]
    [InlineData("stale-candidate", "pending")]
    [InlineData("unknown-main", "pending")]
    [InlineData("main-unavailable", "pending")]
    [InlineData("current-pass", "passed")]
    [InlineData("landed", "failed")]
    public async Task RetainedFailure_RequiresExpectedMainInPreviewAndDetail(string scenario, string expected)
    {
        var original = Fixture("changed").ToSnapshot();
        var retainedCandidate = scenario == "stale-candidate" ? Next : Candidate;
        var retainedMain = scenario == "unknown-main" ? null : scenario is "stale-main" or "current-pass" ? Next : Basis;
        var goal = new TestGoal(original with
        {
            LatestAcceptanceFailure = new(At.AddMinutes(4), ["retained-check"], retainedCandidate, retainedMain)
        });
        Assert.NotNull(((Goal)goal).RetainedAcceptanceFailure);
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var path = Path.Combine(root, "run-events.db");
            var store = new SqliteRunEventStore(path);
            await AppendAcceptanceAsync(store, goal.Id, "failed", retainedCandidate, retainedMain ?? Next);
            if (scenario == "current-pass") await AppendAcceptanceAsync(store, goal.Id, "passed", Candidate, Basis);
            var reads = 0;
            var query = new OwnerGoalChangeSummaryQuery(
                promotion: (_, _) => Task.FromResult<GoalPromotionFact?>(scenario == "landed" ? Promotion(goal) : null),
                acceptance: async (id, candidate, landed, token) =>
                {
                    reads++;
                    Assert.Equal(goal.Id, id); Assert.Equal(Candidate, candidate);
                    if (scenario == "main-unavailable") throw new IOException("Integration revision unavailable.");
                    var main = landed?.PriorMainRevision ?? Basis;
                    return new OwnerGoalAcceptanceEvidence(main,
                        await GoalOperationJournal.ReadNewestAcceptanceOutcomeAsync(path, id, candidate, main, token));
                });
            var (preview, detail) = await RenderSelectedGoalAsync(goal, query);
            Assert.Equal(2, reads);
            if (expected is "passed" or "failed") Assert.Contains("gate " + expected, preview);
            else Assert.DoesNotContain(" · ", preview);
            Assert.Contains("Verified: " + expected, detail);
            Assert.DoesNotContain("Landing: last gate failed", detail);
            if (expected == "failed") Assert.Contains("retained-check", detail);
            else
            {
                Assert.DoesNotContain("Verified: failed", detail);
                Assert.DoesNotContain("retained-check", detail);
            }
            if (scenario is "stale-main" or "current-pass")
            {
                Assert.Contains("Retained acceptance failure superseded", detail);
                Assert.Contains("recorded main " + Next + "; expected main " + Basis, detail);
            }
            if (scenario == "main-unavailable") Assert.Contains("Retained acceptance failure applicability unavailable", detail);
            if (scenario == "landed") Assert.Contains("Promotion: landed", detail);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("amended")]
    [InlineData("criteria")]
    [InlineData("missing")]
    [InlineData("oversized")]
    [InlineData("malformed")]
    [InlineData("mapping")]
    public async Task InvalidPlan_FallsBackWithCurrentAuthority(string fault)
    {
        var goal = Fixture("planned");
        var snapshot = goal.ToSnapshot();
        if (fault == "amended")
            snapshot = snapshot with { Objective = "New brief", BriefVersions = [new(1, goal.Objective, At, SupersededByVersion: 2), new(2, "New brief", At.AddMinutes(1))] };
        if (fault == "criteria")
            snapshot = snapshot with { Tasks = snapshot.Tasks.Select(task => task.RequiredRole != AgentRole.Planner ? task : task with
                { LastVerification = task.LastVerification! with { AcceptanceCriteriaVersionHash = "old" } }).ToArray() };
        goal = new TestGoal(snapshot);
        var reads = 0;
        var query = new OwnerGoalChangeSummaryQuery(artifact: (record, token) =>
        {
            reads++;
            return Task.FromResult(fault switch
            {
                "missing" => null,
                "oversized" => new string('x', OwnerGoalChangeSummaryQuery.MaxArtifactBytes + 1),
                "malformed" => "not a durable plan",
                "mapping" => Plan.Replace("1. disposition", "9. disposition", StringComparison.Ordinal),
                _ => Plan
            });
        });
        var detail = await new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: query).LoadAsync(goal.Id.Value, Token);
        var output = new OwnerConsoleCapturedOutput();
        OwnerConsoleGoalDetailFormatter.Render(detail!, output, TimeZoneInfo.Utc);
        Assert.Contains("Expected from brief", output.Text);
        Assert.DoesNotContain("completed plan", output.Text);
        Assert.Contains("planning " + (fault is "amended" or "criteria" or "mapping" ? "stale" : "unavailable"), output.Text);
        Assert.Equal(fault is "amended" or "criteria" ? 0 : 1, reads);
    }

    [Fact]
    public async Task CandidateAndReceiptUpdates_InvalidateSectionsWithoutOldGreen()
    {
        var goal = Fixture("passed");
        var queries = new Queries(goal);
        var comparisons = new List<string>();
        var summary = new OwnerGoalChangeSummaryQuery(changes: (basis, candidate, token) =>
        {
            comparisons.Add(candidate);
            return Task.FromResult(new CommittedGoalChanges(true, [], 0));
        }, mergeBase: (_, _) => Task.FromResult<string?>(Basis));
        var query = new OwnerGoalDetailQuery(queries, new EmptyTail(), summary: summary);
        Assert.StartsWith("passed", (await query.LoadSummaryAsync(goal.Id.Value, Token))!.Verified.State);
        var snapshot = goal.ToSnapshot();
        queries.Goal = new TestGoal(snapshot with { Tasks = snapshot.Tasks.Select(task => task.RequiredRole != AgentRole.Developer ? task : task with
            { LastDispatch = task.LastDispatch! with { ResultCommit = Next }, DispatchHistory = null }).ToArray() });
        var updated = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.Equal(Next, updated.Candidate);
        Assert.StartsWith("pending", updated.Verified.State);
        Assert.StartsWith("unavailable", updated.Reported.State);
        Assert.Equal(new[] { Candidate, Next }, comparisons);
        snapshot = queries.Goal.ToSnapshot();
        queries.Goal = new TestGoal(snapshot with { CriterionEvidenceObligations = [Obligation(Next, true)] });
        Assert.StartsWith("passed", (await query.LoadSummaryAsync(goal.Id.Value, Token))!.Verified.State);
        Assert.Equal(2, comparisons.Count); // A receipt-only update doesn't re-run either diff.
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ReportedSummary_UsesBoundAutoCommitAndRejectsContradictions(bool autoCommit)
    {
        var goal = Fixture("changed");
        var snapshot = goal.ToSnapshot();
        goal = new TestGoal(snapshot with { Tasks = snapshot.Tasks.Select(task => task.RequiredRole != AgentRole.Developer ? task : task with
        {
            LastVerification = task.LastVerification! with
                { StandardOutput = task.LastVerification.StandardOutput.Replace("commit: " + Candidate, "commit: " + (autoCommit ? "none" : Next), StringComparison.Ordinal) }
        }).ToArray() });
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail());
        var summary = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.StartsWith(autoCommit ? "worker-reported" : "unavailable", summary.Reported.State);
        if (autoCommit) Assert.Contains(Candidate, summary.Reported.Source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DiffAvailability_EmptyAndUnavailableStayDistinct(bool available)
    {
        var goal = Fixture("changed");
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: new(changes: (_, _, _) =>
            Task.FromResult(new CommittedGoalChanges(available, [], 0, "objects missing after cleanup"))));
        var result = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.Contains(available ? "empty committed range" : "unavailable", result.Changed.State);
        Assert.DoesNotContain("passed", result.Verified.State);
    }

    [Fact]
    public async Task Cleanup_UsesPromotionAndKeepsMissingVerificationExplicit()
    {
        var goal = Fixture("changed");
        var query = new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: new(
            (_, _) => Task.FromResult<GoalPromotionFact?>(Promotion(goal)),
            (_, _, _) => Task.FromResult(new CommittedGoalChanges(false, [], 0, "objects unavailable"))));
        var result = (await query.LoadSummaryAsync(goal.Id.Value, Token))!;
        Assert.StartsWith("landed", result.Landing);
        Assert.StartsWith("unavailable", result.Changed.State);
        Assert.StartsWith("pending", result.Verified.State);
    }
}
