using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: each integration case owns its repository and stores; console capture is async-local.
public sealed class DogfoodRetainedCandidateRedProjectionTests : GoalWorktreeTestBase
{
    private static readonly string Candidate = new('a', 40);
    private static readonly string Main = new('b', 40);
    private static readonly DateTimeOffset At = new(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Projection_FailedThenRegated_RetainsPairBoundFailure(bool tiedTimestamps)
    {
        var goal = new Goal(GoalId.New(), "Retain executed failure", [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
        var first = Entry(goal, "first", GoalOperationStatus.Failed, "failed");
        var rerun = first with { IdempotencyKey = "rerun", At = tiedTimestamps ? At : At.AddMinutes(1) };
        var regate = first with
        {
            IdempotencyKey = "regate", Status = GoalOperationStatus.Completed,
            AcceptanceOutcome = "apparatus-regate", At = tiedTimestamps ? At : At.AddMinutes(2)
        };
        var evidence = GoalLandingPostActions.ResolveDogfoodLandingEvidence(Journal(first, rerun, regate));

        Assert.Equal(DogfoodAcceptanceDisposition.Inconclusive, evidence.AcceptanceDisposition);
        Assert.Equal("apparatus-regate", evidence.AcceptanceOutcome);
        Assert.Equal("apparatus-regate", evidence.LatestSchedulingDisposition);
        Assert.Equal(DogfoodExecutedCandidateResult.Failed, evidence.ExecutedCandidateResult);
        Assert.Equal(DogfoodBaselineState.Unknown, evidence.BaselineState);
        Assert.Null(evidence.IsParked);
        Assert.Contains("Executed candidate RED retained:", DogfoodLogRenderer.Render(goal, evidence).Summary);
    }

    [Theory]
    [InlineData("regate-only")]
    [InlineData("aborted-only")]
    [InlineData("blocked-failed")]
    [InlineData("other-branch")]
    [InlineData("other-main")]
    [InlineData("null-pair")]
    public void Projection_WithoutMatchingExecutedFailure_PreservesPriorWording(string scenario)
    {
        var goal = new Goal(GoalId.New(), "Unexecuted acceptance", [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
        var earlier = Entry(goal, "earlier", GoalOperationStatus.Failed, "failed");
        var newest = Entry(goal, "newest", GoalOperationStatus.Completed, "apparatus-regate")
            with { At = At.AddMinutes(1) };
        GoalOperationJournalEntry[] entries = scenario switch
        {
            "regate-only" => [newest],
            "aborted-only" => [newest with { Status = GoalOperationStatus.Aborted, AcceptanceOutcome = "aborted:state-guard" }],
            "blocked-failed" => [earlier with { AcceptanceOutcome = "blocked:timeout" }, newest],
            "other-branch" => [earlier with { BranchHeadSha = new string('c', 40) }, newest],
            "other-main" => [earlier with { MainHeadSha = new string('c', 40) }, newest],
            "null-pair" => [earlier with { BranchHeadSha = null, MainHeadSha = null },
                newest with { BranchHeadSha = null, MainHeadSha = null }],
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        var evidence = GoalLandingPostActions.ResolveDogfoodLandingEvidence(Journal(entries));
        var summary = DogfoodLogRenderer.Render(goal, evidence).Summary;

        Assert.Equal(new DogfoodLandingEvidence(
            DogfoodAcceptanceDisposition.Inconclusive, entries[^1].AcceptanceOutcome), evidence);
        Assert.EndsWith($"Acceptance inconclusive: {entries[^1].AcceptanceOutcome}.", summary);
        Assert.DoesNotContain("Executed candidate", summary);
        Assert.DoesNotContain("Baseline:", summary);
        Assert.DoesNotContain("Parked", summary);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DogfoodCommands_RegatedFailure_PersistRetainedRedAndParkedState(bool parked)
    {
        var repo = CreateSeededRepository();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            var goal = kernel.CreateGoal("Record retained failure", [new TaskSpec(TaskId.New(), "Work", AgentRole.Developer)]);
            GoalOperationJournal.AcceptanceFailed(repo, goal, "first", Candidate, Main);
            GoalOperationJournal.AcceptanceFailed(repo, goal, "rerun", Candidate, Main);
            GoalOperationJournal.AcceptanceApparatusRegated(repo, goal, Candidate, Main,
                "test-receipt", ["FailingCase"], ["process-spawning"], 1);
            if (parked) kernel.ParkGoal(goal.Id, "Await a later acceptance attempt.");
            Assert.Equal(parked, goal.IsExplicitlyParked);
            var journal = GoalOperationJournal.Read(repo, goal.Id);
            var before = JsonSerializer.Serialize(journal);
            var pair = GoalOperationJournal.AcceptanceOutcomesForCandidate(journal, Candidate, Main);
            Assert.Equal(3, pair.Count);
            Assert.Equal("apparatus-regate", pair[0].AcceptanceOutcome);
            Assert.Equal(2, pair.Count(entry => entry.Status == GoalOperationStatus.Failed && entry.AcceptanceOutcome == "failed"));
            var workspace = OrchestratorWorkspace.ForDirectory(repo);
            var context = CreateAcceptanceContext(kernel, repo, goal);
            var add = InfrastructureTestSupport.CaptureConsole(() =>
                CliCommandHandlers.Execute(["dogfood-log", "add", goal.Id.Value], context));
            var stored = Assert.Single(await new DogfoodLogStore(workspace.DogfoodLogStorePath).ListRecentAsync());
            var list = InfrastructureTestSupport.CaptureConsole(() =>
                CliCommandHandlers.Execute(["dogfood-log", "list"], context));

            Assert.Contains("Executed candidate RED retained:", stored.RenderedMarkdown);
            Assert.Contains("Acceptance inconclusive: apparatus-regate.", stored.Summary);
            Assert.Contains("Baseline: unknown.", stored.Summary);
            Assert.Equal(parked, stored.Summary.Contains("Parked.", StringComparison.Ordinal));
            Assert.Contains(stored.RenderedMarkdown, add);
            Assert.Contains(stored.RenderedMarkdown, list);
            Assert.DoesNotContain("Acceptance passed", list, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gate passed", list, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("gate-passed", list, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("introduced", list, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Baseline: executed", list, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(before, JsonSerializer.Serialize(GoalOperationJournal.Read(repo, goal.Id)));
        }
        finally
        {
            DeleteDirectory(repo);
        }
    }

    private static GoalOperationJournalEntry Entry(
        Goal goal, string id, GoalOperationStatus status, string outcome) =>
        new(id, goal.Id, "acceptance", status, At, null, Candidate, Main, outcome);

    private static GoalOperationJournalSummary Journal(params GoalOperationJournalEntry[] entries) =>
        new("unused", entries, entries, []);
}
