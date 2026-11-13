using Mcg.AgentOrchestrator.App.OwnerConsole;
using Mcg.AgentOrchestrator.Core;
using Terminal.Gui.App;
using Terminal.Gui.Input;
using Terminal.Gui.Text;
using Terminal.Gui.Views;

// Parallel-safe: state, questions, clocks and the uninitialized GUI app are per test.
public sealed class OwnerConsoleBoardAttentionTests
{
    [Fact]
    public async Task EveryRankKeepsStageTextAndSelectionAcrossRefresh()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock();
        foreach (var id in new[] { "11111111", "22222222", "33333333", "44444444", "55555555", "66666666", "77777777" })
            harness.AddGoal(id, id, AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = goal.Id switch
                {
                    "55555555" => GoalStatus.Verifying,
                    "44444444" => GoalStatus.Verified,
                    "11111111" => GoalStatus.Failed,
                    _ => GoalStatus.Active
                },
                Tasks = goal.Tasks.Select(task => task with
                { Status = goal.Id == "33333333" ? WorkTaskStatus.Running : WorkTaskStatus.Pending }).ToArray(),
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "fixture failure", clock.Now.AddMinutes(-6))]
            }).ToArray()
        });
        harness.Kernel.ObserveGoalHold(new("66666666"), "dependency", "waiting on dependency 71bb0340abcdef",
            clock.Now, TimeSpan.FromMinutes(10));
        harness.Questions.Items.Add(new("q1", "77777777", OwnerQuestionKind.HumanInput, "Proceed?"));
        var builder = Builder(harness, clock);
        var model = await builder.BuildAsync(new(clock.Now, null, [], 0));
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness, clock);
        view.Render(model);

        Assert.Equal(Enum.GetValues<OwnerConsoleAttention>(), model.Board.Select(row => row.SortKey
            .WithOwnerQuestion(model.Decisions.Any(decision => decision.GoalId == row.GoalId)).Rank));
        Assert.Equal(["77777777", "66666666", "55555555", "44444444", "33333333", "22222222", "11111111"],
            model.Board.Select(row => row.GoalId));
        Assert.Equal(["question for you", "held: depends on 71bb0340", "gate running 6m", "waiting for gate (1st in queue)",
            "in progress: Developer", "in progress: Developer", "failed: fixture failure"], model.Board.Select(row => row.Stage));
        view.FocusPane(OwnerConsolePane.Board);
        view.SelectBoard(3);
        Assert.Equal("44444444", view.SelectedGoalId);
        harness.Questions.Items.Clear();

        view.Render(await builder.BuildAsync(new(clock.Now, null, [], 0)));

        Assert.Equal("44444444", view.SelectedGoalId);
        Assert.Equal(2, ((IOwnerConsoleKeySurface)view).SelectedBoardIndex);
    }

    [Fact]
    public void OrderKeyExplainsRanksAndTieBreaks()
    {
        var labels = Enum.GetValues<OwnerConsoleAttention>().Select(rank => rank switch
        {
            OwnerConsoleAttention.NeedsYou => "needs you",
            OwnerConsoleAttention.Held => "held",
            OwnerConsoleAttention.GateRunning => "running in shared gate",
            OwnerConsoleAttention.WaitingForGate => "waiting for shared gate",
            OwnerConsoleAttention.WorkerRunning => "worker running",
            OwnerConsoleAttention.Other => "other",
            OwnerConsoleAttention.RecentTerminal => "recently ended",
            _ => throw new ArgumentOutOfRangeException(nameof(rank))
        }).ToArray();
        foreach (var text in new[] { OwnerConsoleBoardOrderKey.Full, OwnerConsoleBoardOrderKey.Compact })
        {
            var previous = -1;
            foreach (var label in labels)
            {
                var index = text.IndexOf(label, StringComparison.Ordinal);
                Assert.True(index > previous, $"Missing or out-of-order rank label: {label}");
                previous = index;
            }
            Assert.Contains("Display order", text);
            Assert.Contains("not scheduler order", text);
            Assert.DoesNotContain("cohort", text, StringComparison.OrdinalIgnoreCase);
        }
        foreach (var phrase in new[] { "shared gate waiters oldest first", "recently ended newest first", "case-insensitive goal id" })
        {
            Assert.Contains(phrase, OwnerConsoleBoardOrderKey.Full);
            Assert.DoesNotContain(phrase, OwnerConsoleBoardOrderKey.Compact);
        }
    }

    [Fact]
    public async Task RenderedOrderKeyFitsThreeWidthsWithoutDisplacingSelection()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock();
        for (var index = 1; index <= 40; index++) harness.AddGoal(index.ToString("D8"), "Goal", AgentRole.Developer);
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness, clock);
        view.Render(await Builder(harness, clock).BuildBoardAsync());
        view.FocusPane(OwnerConsolePane.Board);
        var surface = (IOwnerConsoleKeySurface)view;
        var initialWidth = OwnerConsoleBoardOrderKey.Full.GetColumns() + 256;
        view.Window.Layout(new System.Drawing.Size(initialWidth, 30));
        var overhead = initialWidth - surface.HintWidth;
        Assert.True(surface.HintWidth > 0);

        foreach (var width in new[] { OwnerConsoleBoardOrderKey.Full.GetColumns(), OwnerConsoleBoardOrderKey.Compact.GetColumns(), 60 })
        {
            view.Window.Layout(new System.Drawing.Size(width + overhead, 30));
            Assert.Equal(width, surface.HintWidth);
            var expected = width >= OwnerConsoleBoardOrderKey.Full.GetColumns() ? OwnerConsoleBoardOrderKey.Full :
                width >= OwnerConsoleBoardOrderKey.Compact.GetColumns() ? OwnerConsoleBoardOrderKey.Compact :
                OwnerConsoleLineFitter.Fit(OwnerConsoleBoardOrderKey.Compact, OwnerConsoleLineSpans.None, width);
            Assert.StartsWith(expected + "  ", view.HintText);
            Assert.True(expected.GetColumns() <= width);
            if (width == 60) Assert.EndsWith("…", expected);
            Assert.DoesNotContain('\n', view.HintText);
            Assert.DoesNotContain('\r', view.HintText);
            Assert.EndsWith(OwnerConsoleKeyHints.Hint(OwnerConsolePane.Board), view.HintText);
            var renderedHint = view.HintText;
            view.RenderHints();
            Assert.Equal(renderedHint, view.HintText);
            view.RenderHints();
            Assert.Equal(renderedHint, view.HintText);
            var bodyRows = view.BoardBodyRows();
            Assert.True(bodyRows > 0 && bodyRows < 40);
            await view.HandleKeyAsync(Key.End);
            Assert.Equal("00000040", view.SelectedGoalId);
            Assert.InRange(surface.SelectedBoardIndex, view.BoardPane.RowOffset, view.BoardPane.RowOffset + bodyRows - 1);
            view.FocusPane(OwnerConsolePane.Activity);
            Assert.DoesNotContain("Display order", view.HintText);
            Assert.Equal(bodyRows, view.BoardBodyRows());
            view.FocusPane(OwnerConsolePane.Board);
            view.EditingCommand = true;
            surface.CompletionHint = "completion fixture";
            view.RenderHints();
            Assert.Equal("completion fixture", view.HintText);
            surface.CompletionHint = null;
            view.EditingCommand = false;
            view.RenderHints();
            Assert.StartsWith(expected + "  ", view.HintText);
        }
    }

    [Fact]
    public async Task VerifiedCohortMembersShowProgressAndDoNotConsumeQueuePositions()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock();
        foreach (var id in new[] { "11111111", "22222222", "33333333", "44444444" })
            harness.AddGoal(id, "Gate fixture", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = GoalStatus.Verified,
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "fixture", clock.Now.AddMinutes(-1))],
                Tasks = goal.Tasks.Select(task => task with { Status = WorkTaskStatus.Completed }).ToArray()
            }).ToArray()
        });
        var gate = new OwnerConsoleGateProgress(true, "cohort", [new("11111111", "First"), new("33333333", "Third")],
            clock.Now.AddMinutes(-20), TimeSpan.FromMinutes(20), [], .7, 360, 24, 33);
        var model = await new OwnerConsoleViewModelBuilder(harness.State, harness.Questions, harness.Liveness,
            new GateFixtureEpics(), clock, gateProgress: _ => gate).BuildBoardAsync();

        Assert.Equal("in gate: 24/33, ~6m left", model.Board.Single(row => row.GoalId == "11111111").Stage);
        Assert.Equal("in gate: 24/33, ~6m left", model.Board.Single(row => row.GoalId == "33333333").Stage);
        var queued = model.Board.Where(row => row.GoalId is "22222222" or "44444444").ToArray();
        Assert.Equal(["queued for gate #1, behind 11111111", "queued for gate #2, behind 11111111"], queued.Select(row => row.Stage));
        Assert.All(queued, row => Assert.DoesNotContain("gate running", row.Stage));
    }

    private sealed class GateFixtureEpics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult("");
    }

    [Fact]
    public async Task BoardOrdersFiveGoalsByAttentionAndNamesEachReason()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock();
        harness.AddGoal("11111111", "Worker", AgentRole.Developer);
        harness.AddGoal("22222222", "Queued", AgentRole.Developer);
        harness.AddGoal("33333333", "Gate", AgentRole.Developer);
        harness.AddGoal("44444444", "Dependency", AgentRole.Developer);
        harness.AddGoal("55555555", "Question", AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = goal.Id switch { "22222222" => GoalStatus.Verified, "33333333" => GoalStatus.Verifying, _ => GoalStatus.Active },
                Tasks = goal.Tasks.Select(task => task with
                { Status = goal.Id == "11111111" ? WorkTaskStatus.Running : WorkTaskStatus.Completed }).ToArray(),
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "state changed", clock.Now.AddMinutes(-6))]
            }).ToArray()
        });
        harness.Kernel.ObserveGoalHold(new("44444444"), "dependency", "waiting on dependency 71bb0340abcdef",
            clock.Now, TimeSpan.FromMinutes(10));
        harness.Questions.Items.Add(new("q1", "55555555", OwnerQuestionKind.HumanInput, "Proceed?"));

        var model = await Builder(harness, clock).BuildAsync(new(clock.Now, null, [], 0));

        Assert.Equal(["55555555", "44444444", "33333333", "22222222", "11111111"], model.Board.Select(row => row.GoalId));
        Assert.Equal(["question for you", "held: depends on 71bb0340", "gate running 6m", "waiting for gate (1st in queue)", "in progress: Developer"],
            model.Board.Select(row => row.Stage));
        Assert.All(model.Board, row => Assert.False(row.Dimmed));
        Assert.Equal(1, harness.State.Calls.Count(call => call == "metadata"));
        Assert.Equal(1, harness.State.Calls.Count(call => call == "goals"));
    }

    [Fact]
    public async Task GateQueueOrdersOldestFirstAndUsesGoalIdForTies()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock();
        foreach (var id in new[] { "11111111", "33333333", "22222222" }) harness.AddGoal(id, id, AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = GoalStatus.Verified,
                Timeline = [new(goal.Id, null, ProgressKind.GoalPolicyDecision, "queued",
                    clock.Now.AddMinutes(goal.Id == "11111111" ? -1 : -10))]
            }).ToArray()
        });

        var model = await Builder(harness, clock).BuildAsync(new(clock.Now, null, [], 0));

        Assert.Equal(["22222222", "33333333", "11111111"], model.Board.Select(row => row.GoalId));
        Assert.Equal(["waiting for gate (1st in queue)", "waiting for gate (2nd in queue)", "waiting for gate (3rd in queue)"],
            model.Board.Select(row => row.Stage));
    }

    [Fact]
    public async Task RecentFailuresAndParksTrailActiveRowsWithReasonsAndDimStyling()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock();
        foreach (var id in new[] { "11111111", "22222222", "33333333", "99999999" }) harness.AddGoal(id, id, AgentRole.Developer);
        var snapshot = harness.Kernel.ExportSnapshot();
        harness.Kernel.ReplaceWithSnapshot(snapshot with
        {
            Goals = snapshot.Goals.Select(goal => goal with
            {
                Status = goal.Id switch { "11111111" or "22222222" => GoalStatus.Failed, "33333333" => GoalStatus.Parked, _ => GoalStatus.Active },
                Timeline = goal.Id == "99999999" ? [] :
                [new(goal.Id, goal.Tasks[0].Id, goal.Id == "33333333" ? ProgressKind.GoalPolicyDecision : ProgressKind.TaskFailed,
                    goal.Id == "33333333" ? "waiting for budget" : "explicit escalation\nmore detail",
                    clock.Now.AddHours(goal.Id == "22222222" ? -48 : goal.Id == "33333333" ? -2 : -1))]
            }).ToArray()
        });
        var model = await Builder(harness, clock).BuildAsync(new(clock.Now, null, [], 0));
        using IApplication app = Terminal.Gui.App.Application.Create();
        using var view = View(app, harness, clock);
        view.Render(model);

        Assert.Equal(["99999999", "11111111", "33333333"], model.Board.Select(row => row.GoalId));
        Assert.False(model.Board[0].Dimmed);
        Assert.True(model.Board[1].Dimmed);
        Assert.True(model.Board[2].Dimmed);
        Assert.Equal("failed: explicit escalation", model.Board[1].Stage);
        Assert.Equal("parked: waiting for budget", model.Board[2].Stage);
        Assert.Equal(1, model.Status.ActiveGoals);
        Assert.Equal(1, model.Status.FailedToday);
        Assert.Contains("failed today: 1", view.StatusText);
        Assert.Equal("failed: explicit escalation", view.BoardTable.Rows[1]["STATUS"]);
        var colors = view.BoardPane.Style.RowColorGetter!;
        Assert.Null(colors(ColorArgs(view, 0)));
        Assert.Equal(view.BoardPane.GetScheme().Disabled, colors(ColorArgs(view, 1))!.Normal);
    }

    [Fact]
    public async Task QuestionsOverlayAndClearWithoutLosingTheUnderlyingStage()
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock();
        harness.AddGoal("11111111", "Worker", AgentRole.Developer);
        harness.AddGoal("22222222", "Dependency", AgentRole.Developer);
        harness.Kernel.ObserveGoalHold(new("22222222"), "dependency", "dependency escalated: 71bb0340",
            clock.Now, TimeSpan.FromMinutes(10));
        var builder = Builder(harness, clock);
        var board = await builder.BuildBoardAsync();
        harness.Questions.Items.Add(new("q1", "11111111", OwnerQuestionKind.Clarification, "Choose?"));
        var (decisions, hidden) = await builder.ReadDecisionsAsync(default);
        var overlay = builder.WithDecisions(board, decisions, hidden);
        Assert.Equal(["11111111", "22222222"], overlay.Board.Select(row => row.GoalId));
        Assert.Equal("question for you", overlay.Board[0].Stage);

        var cleared = builder.WithDecisions(overlay, [], 0);

        Assert.Equal(["22222222", "11111111"], cleared.Board.Select(row => row.GoalId));
        Assert.Equal("held: depends on 71bb0340", cleared.Board[0].Stage);
        Assert.Equal("in progress: Developer", cleared.Board[1].Stage);
    }

    [Theory]
    [InlineData("waiting on dependency 71bb0340", "held: depends on 71bb0340")]
    [InlineData("dependency-terminal-without-landing: 71bb0340 state=Failed", "held: depends on 71bb0340")]
    [InlineData("waiting_for_approval", "held: waiting for your approval")]
    public async Task HoldWordingUsesDependencyPrefixesOrTheDetailNarrator(string blocker, string expected)
    {
        var harness = new OwnerConsoleHarness();
        var clock = new OwnerConsoleTestClock();
        var goal = harness.AddGoal("11111111", "Held", AgentRole.Developer);
        harness.Kernel.ObserveGoalHold(goal.Id, "blocked", blocker, clock.Now, TimeSpan.FromMinutes(10));

        var model = await Builder(harness, clock).BuildBoardAsync();

        Assert.Equal(expected, Assert.Single(model.Board).Stage);
    }

    private static OwnerConsoleViewModelBuilder Builder(OwnerConsoleHarness harness, TimeProvider clock) =>
        new(harness.State, harness.Questions, harness.Liveness, new Epics(), clock);

    // Terminal.Gui constructs these args internally; invoke the real row styling hook without a terminal driver.
    private static RowColorGetterArgs ColorArgs(OwnerConsoleFullScreenView view, int row) =>
        (RowColorGetterArgs)Activator.CreateInstance(typeof(RowColorGetterArgs),
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic,
            null, [view.BoardPane.Table!, row], null)!;

    private static OwnerConsoleFullScreenView View(IApplication app, OwnerConsoleHarness harness, TimeProvider clock) =>
        new(app, OwnerConsoleControllerFactory.Create(harness.Questions, harness.Answers, new Dialogs(), harness.State, harness.Tail,
            harness.Conductor, harness.DigestReport, harness.Digest, clock), () => Task.CompletedTask, clock: clock);

    private sealed class Epics : IOwnerGoalEpicLookup
    {
        public Task<string> GetTitleAsync(string id, CancellationToken token) => Task.FromResult(string.Empty);
    }

    private sealed class Dialogs : IOwnerConsoleDialogs
    {
        public Task<bool> ConfirmAsync(string title, string text) => Task.FromResult(false);
        public Task<string?> PromptTextAsync(string title, string text) => Task.FromResult<string?>(null);
        public Task ShowTextAsync(string title, string text) => Task.CompletedTask;
    }
}
