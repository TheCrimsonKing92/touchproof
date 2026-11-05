using System.Data;
using System.Text.Json;
using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.Views;
using static OwnerConsoleGateExecutionReplayFixture;

// Parallel-safe: fixed replay clock, in-memory state and private lane-plan files.
public sealed class GateExecutionProjectionCompletionTests
{
    private const string First = "2a0c2f63";
    private const string Second = "59edd02c";
    private const string Third = "be7cc3a4";
    private const string CohortOuter = "2dd6cee4517d4635a67815a7c5c56bb5";
    private const string TrainOuter = "2bd7cb7a12b6453e969d0f4424d5e307";
    private const string CohortExecution = "operator-gate-20261104232553958-4c4f3804";
    private const string TrainExecution = "2a0c2f63-gate-20261105000044171-7de676907de676907de676907de67690";
    private const string Pair = First + "," + Second;
    private const string Trio = Pair + "," + Third;

    [Fact]
    public void AttemptOnlyCompletionAndTrainMarkersLeaveOnlyTheTrainLive()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var events = Incident();
        var gate = fixture.Build(events);

        Assert.False(gate.ForExecution(CohortExecution)!.Live);
        Assert.Equal(TrainExecution, Assert.Single(gate.Executions!, run => run.Live).ExecutionId);
        foreach (var member in Trio.Split(','))
        {
            var selected = Assert.IsType<OwnerConsoleGateProgress>(gate.ForMember(member));
            Assert.True(selected.Live);
            Assert.Equal("train", selected.Kind);
            Assert.Equal(TrainExecution, selected.ExecutionId);
            Assert.Equal(Trio.Split(','), selected.Members.Select(item => item.Id));
            Assert.Null(selected.ConcurrentRuns);
        }
        Assert.DoesNotContain(gate.Executions!, run => run.Live && run.ExecutionId is null);

        // Removing the completion keeps the actual cohort live for its two
        // members, but never adds that cohort to the train's third member.
        var withoutCompletion = fixture.Build(events.Where(item =>
            !item.Detail.StartsWith("ACCEPTANCE_COHORT_CHILD_COMPLETED", StringComparison.Ordinal)));
        Assert.True(withoutCompletion.ForExecution(CohortExecution)!.Live);
        Assert.Equal(2, withoutCompletion.ForMember(First)!.ConcurrentRuns!.Count);
        Assert.Equal(TrainExecution, withoutCompletion.ForMember(Third)!.ExecutionId);
    }

    [Theory]
    [InlineData("ACCEPTANCE_COHORT_CHILD_COMPLETED", "")]
    [InlineData("ACCEPTANCE_COHORT_RECONCILED_DEAD", "")]
    [InlineData("ACCEPTANCE_COHORT_ADOPTION_REFUSED", "")]
    [InlineData("ACCEPTANCE_COHORT_ABANDONED", "")]
    [InlineData("ACCEPTANCE_COHORT_EXIT", " outcome=passed")]
    public void TerminalOuterAttemptClosesItsChildrenOnly(string name, string suffix)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var events = new[]
        {
            Run(-300, CohortExecution, CohortOuter, Pair, "verification-check"),
            Run(-200, TrainExecution, TrainOuter, Trio, "verification-check", "merge-train"),
            Event(-100, $"{name} outer_attempt={CohortOuter} members={Pair}{suffix}"),
            Run(-50, CohortExecution, CohortOuter, Pair, "verification-check"),
            Run(-40, "late-cohort-child", CohortOuter, Pair, "verification-check")
        };
        var gate = fixture.Build(events);
        Assert.False(gate.ForExecution(CohortExecution)!.Live);
        Assert.False(gate.ForExecution("late-cohort-child")!.Live);
        Assert.True(gate.ForExecution(TrainExecution)!.Live);
        Assert.Equal(TrainExecution, gate.ForMember(First)!.ExecutionId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConcurrentRunsHaveSeparateGateAndBoardLines(bool solo)
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var members = solo ? First : Pair;
        fixture.WritePlan(CohortExecution, ["Console"]);
        fixture.WritePlan(TrainExecution, ["Console", "Console:expanded"]);
        var gate = fixture.Build([
            Run(-300, CohortExecution, CohortOuter, members, "verification-check"),
            Plan(-290, CohortExecution, CohortOuter, members, fixture),
            Run(-280, CohortExecution, CohortOuter, members, "shard-complete") with
                { Detail = Run(-280, CohortExecution, CohortOuter, members, "shard-complete").Detail + " outcome=failed" },
            Run(-200, TrainExecution, TrainOuter, Trio, "verification-check", "merge-train"),
            Plan(-190, TrainExecution, TrainOuter, Trio, fixture, "merge-train")
        ]);
        var selected = gate.ForMember(First)!;
        var runs = selected.ConcurrentRuns!;
        Assert.Equal(2, runs.Count);
        Assert.Equal(new[] { CohortExecution, TrainExecution }.Order(StringComparer.Ordinal), runs.Select(run => run.ExecutionId));
        Assert.Contains(runs, run => run.ExecutionId == CohortExecution && run.Kind == (solo ? "solo" : "cohort"));
        Assert.Contains(runs, run => run.ExecutionId == TrainExecution && run.Kind == "train");
        var expected = new[]
        {
            $"{(solo ? "solo" : "cohort")} {members.Replace(',', '+')} failing 1/1",
            $"train {Trio.Replace(',', '+')} running 0/2"
        };
        var fields = new Dictionary<int, OwnerConsoleFieldSnapshot>();
        var lines = OwnerConsoleGateLayout.Lines(selected, 240, false, fields);
        Assert.Equal(3, lines.Count);
        foreach (var text in expected) Assert.Single(lines, line => line.StartsWith(text, StringComparison.Ordinal));
        Assert.Equal(2, fields.Count);
        Assert.DoesNotContain("select an execution", string.Join('\n', lines));

        var clock = new OwnerConsoleTestClock { Now = Now };
        var harness = new OwnerConsoleHarness(clock);
        harness.AddGoal(First, "Shared goal", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = GoalStatus.Verifying,
                Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray(),
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "state changed", Now)]
            }).ToArray()
        });
        var builder = new OwnerConsoleViewModelBuilder(harness.State, harness.Questions,
            harness.Liveness, new Epics(), clock, gateProgress: _ => gate);
        var model = builder.WithDecisions(await builder.BuildBoardAsync(), [], 0);
        Assert.Equal(1, model.Status.ActiveGoals);
        Assert.Equal(2, model.Board.Length);
        Assert.All(model.Board, row => Assert.Equal(First, row.GoalId));
        foreach (var text in expected)
            Assert.Single(model.Board, row => row.Stage.StartsWith("in gate: " + text, StringComparison.Ordinal));
        using var table = new DataTable();
        foreach (var column in new[] { "GOAL", "EPIC", "TITLE", "PRIORITY", "STATUS", "AGE" }) table.Columns.Add(column);
        using var view = new TableView { Table = new DataTableSource(table) };
        OwnerConsoleBoardRenderer.Render(table, view, model.Board, 0, 0);
        Assert.Equal(2, table.Rows.Count);
        Assert.Equal(model.Board.Select(row => row.Stage), table.Rows.Cast<DataRow>().Select(row => (string)row["STATUS"]));
        Assert.DoesNotContain("select an execution", string.Join('\n', model.Board.Select(row => row.Stage)));
        var section = OwnerConsoleBoardSection.Create("repo", model.Board, true, null, Now);
        Assert.Contains("1 active", section.Header.Title);
        Assert.Contains("1 in gate", section.Header.Title);
    }

    [Fact]
    public void UnidentifiedExecutionProgressStaysIndependentOfIdentifiedRuns()
    {
        using var fixture = new OwnerConsoleGateExecutionReplayFixture();
        var gate = fixture.Build([
            new(Now.AddSeconds(-300), "gate-progress", Third,
                $"PHASE_PROGRESS phase=verification-check members={Third} target=Console"),
            Run(-200, TrainExecution, TrainOuter, Trio, "verification-check", "merge-train")
        ]);
        var runs = gate.ForMember(Third)!.ConcurrentRuns!;
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, run => run.ExecutionId is null && run.Live);
        Assert.Contains(runs, run => run.ExecutionId == TrainExecution && run.Live);
    }

    private static OwnerConductEvent[] Incident() =>
    [
        Event(-600, $"ACCEPTANCE_COHORT_ENTRY tick=1 members={Pair}"),
        Event(-590, $"ACCEPTANCE_COHORT_INFLIGHT attempt={CohortOuter} members={Pair}"),
        Run(-580, CohortExecution, CohortOuter, Pair, "verification-check"),
        Event(-500, $"ACCEPTANCE_COHORT_CHILD_COMPLETED kind=cohort attempt={CohortOuter} members={Pair} result=completed"),
        Event(-490, $"ACCEPTANCE_COHORT_ENTRY tick=2 members={Trio}"),
        Run(-480, TrainExecution, TrainOuter, Trio, "execution-admitted", "merge-train"),
        Run(-470, TrainExecution, TrainOuter, Trio, "verification-check", "merge-train"),
        Event(-460, $"ACCEPTANCE_COHORT_INFLIGHT tick=2 members={Trio} outcome=inflight"),
        Event(-450, $"ACCEPTANCE_COHORT_ADOPTED kind=train members={Trio} identity=train-identity owner=123"),
        Run(-440, TrainExecution, TrainOuter, Trio, "verification-check", "merge-train") with
            { Detail = Run(-440, TrainExecution, TrainOuter, Trio, "verification-check", "merge-train").Detail +
                " target=\"reviewer focused evidence: Console\"" }
    ];

    private static OwnerConductEvent Run(int age, string execution, string outer, string members,
        string phase, string scope = "cohort") => new(Now.AddSeconds(age), "gate-progress", members.Split(',')[0],
            $"PHASE_PROGRESS execution={execution} outer_attempt={outer} members={members} " +
            $"scope={scope} phase={phase} target=Console elapsed_ms=0");

    private static OwnerConductEvent Plan(int age, string execution, string outer, string members,
        OwnerConsoleGateExecutionReplayFixture fixture, string scope = "cohort") =>
        Run(age, execution, outer, members, "lane-plan", scope) with
        { Detail = Run(age, execution, outer, members, "lane-plan", scope).Detail + " heartbeat=" + JsonSerializer.Serialize(fixture.PlanPath(execution)) };

    private static OwnerConductEvent Event(int age, string detail) => new(Now.AddSeconds(age), "acceptance-cohort", First, detail);

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }
}
