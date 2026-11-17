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

// Parallel-safe: instance-owned snapshots, I/O delegates, event gates and uninitialized GUI views.
public sealed class OwnerConsoleGoalChangeSummaryFieldsAndQueueTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FieldsRetainOrderedShortAndLongSourceItemsAndPreview(bool includeLong)
    {
        var goal = Fixture("changed");
        string[] files = includeLong ? ["M src/short.cs", "A src/" + new string('長', 400) + ".cs", "M src/last.cs"] : ["M src/short.cs"];
        var query = new OwnerGoalChangeSummaryQuery(changes: (_, _, _) =>
            Task.FromResult(new CommittedGoalChanges(true, files, files.Length)),
            mergeBase: (_, _) => Task.FromResult<string?>(Basis));
        var detail = await new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: query).LoadAsync(goal.Id.Value, Token);
        var summary = Assert.IsType<OwnerGoalChangeSummary>(detail!.Changes);
        var fields = summary.Fields().ToArray();
        Assert.Equal(files, fields.Where(field => field.Key.StartsWith("Changed/", StringComparison.Ordinal) &&
            int.TryParse(field.Key[8..], out _)).Select(field => field.Value));
        Assert.Equal(summary.Preview!.Changes.Total.ToString(), fields.Single(field => field.Key == "preview/changes/total").Value);
        Assert.Equal(summary.Preview.Changes.Files, fields.Where(field => field.Key.StartsWith("preview/changes/", StringComparison.Ordinal) &&
            int.TryParse(field.Key[16..], out _)).Select(field => field.Value));
        Assert.Equal(summary.Preview.Evidence?.At.ToString("O"), fields.Single(field => field.Key == "preview/evidence/at").Value);
        var writes = new List<string>();
        using var modal = new OwnerConsoleFieldsDialog(detail.Fields(), new(value => { writes.Add(value); return true; }), includeLong ? "Changed/1" : "Changed/0");
        modal.Copy(); Assert.Equal(files[includeLong ? 1 : 0], Assert.Single(writes));
    }

    [Fact]
    public async Task FieldsKeepFullVerifiedCheckNamesAndPreviewEvidence()
    {
        var check = "Full.Check." + new string('長', 400);
        var goal = new TestGoal(Fixture("changed").ToSnapshot() with {
            LatestAcceptanceFailure = new(At.AddMinutes(4), [check], Candidate, Basis) });
        var query = new OwnerGoalChangeSummaryQuery(acceptance: (_, _, _, _) =>
            Task.FromResult(new OwnerGoalAcceptanceEvidence(Basis, null)));
        var detail = await new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: query).LoadAsync(goal.Id.Value, Token);
        var summary = Assert.IsType<OwnerGoalChangeSummary>(detail!.Changes);
        Assert.DoesNotContain(check, summary.Verified.Items);
        var fields = detail.Fields();
        Assert.Equal(check, fields.Fields.Single(field => field.Key == "Verified/0").Value);
        Assert.Equal(check, fields.Fields.Single(field => field.Key == "preview/evidence/failed").Value);
        Assert.Equal("False", fields.Fields.Single(field => field.Key == "preview/evidence/passed").Value);
        Assert.Equal(At.AddMinutes(4).ToString("O"), fields.Fields.Single(field => field.Key == "preview/evidence/at").Value);
    }

    [Fact]
    public void CommittedDiff_NulPathsPreserveKindsAndFailuresCannotLookEmpty()
    {
        var parsed = GoalChangesReader.ParseCommittedRange(new(0, "M\0src/長い😀.cs\0A\0name\nwith newline\0", ""));
        Assert.True(parsed.Available);
        Assert.Equal(new[] { "M src/長い😀.cs", "A name\nwith newline" }, parsed.Files);
        Assert.Equal(2, parsed.TotalFiles);
        Assert.True(GoalChangesReader.ParseCommittedRange(new(0, "", "")).Available);
        Assert.False(GoalChangesReader.ParseCommittedRange(new(128, "", "missing objects")).Available);
        Assert.False(GoalChangesReader.ParseCommittedRange(new(0, "M\0missing-path", "", DrainTimedOut: true)).Available);
        Assert.False(GoalChangesReader.ParseCommittedRange(new(0, "M\0", "")).Available);
    }

    [Fact]
    public async Task QueueAdapter_ProjectsSchedulingFactsWithoutPeerHydration()
    {
        var root = SharedTestSupport.CreateTempDirectory();
        try
        {
            var kernel = new AgentOrchestratorKernel();
            for (var index = 0; index < 4; index++)
                kernel.CreateGoal(new GoalId($"queue-{index}"), "Queue goal", [new(TaskId.New(), "Build", AgentRole.Developer)]);
            var original = kernel.ExportSnapshot();
            kernel.ReplaceWithSnapshot(original with { Goals = original.Goals.Select((goal, index) => goal with
            {
                Status = GoalStatus.Verified,
                Priority = index == 1 ? "High" : "Normal",
                PriorityScheduling = index == 2 ? new("Normal", GateOvertakes: 3) : null,
                CurrentHold = index == 3 ? new("hold", "waiting", "hold", At) : null,
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "entered Verified", At.AddMinutes(index))]
            }).ToArray() });
            var statePath = Path.Combine(root, "state.db");
            _ = StateDbMigrations.EnsureUpToDate(statePath);
            var repository = new SqliteOrchestratorStateRepository(statePath);
            await repository.SaveAsync(kernel, Token);
            var facts = await SqliteOrchestratorStateRepository.OpenReadOnly(statePath).ListAcceptanceQueueFactsAsync(Token);
            var expected = GoalSchedulingOrder.ForAcceptance(kernel.Goals.Where(goal => goal.CurrentHold is null)).Select(goal => goal.Id.Value);
            var actual = facts.OrderByDescending(item => item.Priority).ThenBy(item => item.VerifiedAt).ThenBy(item => item.CreatedAt)
                .ThenBy(item => item.GoalId, StringComparer.Ordinal).Select(item => item.GoalId);
            Assert.Equal(expected, actual);
            Assert.Equal(3, facts.Count);
        }
        finally { SharedTestSupport.RemoveTempDirectory(root); }
    }

    [Theory]
    [InlineData("passed", true)]
    [InlineData("aborted:state-guard", true)]
    [InlineData("passed", false)]
    public async Task NormalAcceptance_DoesNotEraseOutstandingCriterionEvidence(string outcome, bool failed)
    {
        var goal = Fixture("changed");
        var obligation = Obligation(Candidate, false);
        if (!failed) obligation = obligation with { State = CriterionEvidenceState.Pending, ReceiptId = null };
        goal = new TestGoal(goal.ToSnapshot() with { CriterionEvidenceObligations = [obligation] });
        var entry = new GoalOperationJournalEntry("acceptance-event", goal.Id, "acceptance",
            outcome == "passed" ? GoalOperationStatus.Completed : GoalOperationStatus.Aborted,
            At.AddMinutes(3), "full gate", Candidate, Basis, outcome);
        var summary = new OwnerGoalChangeSummaryQuery(acceptance: (_, _, _, _) =>
            Task.FromResult(new OwnerGoalAcceptanceEvidence(Basis, entry)));
        var detail = await new OwnerGoalDetailQuery(new Queries(goal), new EmptyTail(), summary: summary).LoadAsync(goal.Id.Value, Token);
        var output = new OwnerConsoleCapturedOutput();
        OwnerConsoleGoalDetailFormatter.Render(detail!, output, TimeZoneInfo.Utc);
        Assert.Contains("Verified: " + (failed ? "failed" : "pending"), output.Text);
        Assert.DoesNotContain("Verified: passed", output.Text);
        Assert.Contains("Full acceptance: " + outcome, output.Text);
    }
}
