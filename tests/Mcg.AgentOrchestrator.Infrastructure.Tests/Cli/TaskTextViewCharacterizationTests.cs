using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Infrastructure;
using Xunit;

// Parallel-safe: Capture routes output through AsyncLocal; fixtures have no shared state.
public sealed class TaskTextViewCharacterizationTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    // Literal formats derived from ConsoleViews.TaskDetails.cs at 9ee8a17b8.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PrintTask_WithoutRecords_PreservesFullLineSequence(bool assigned)
    {
        var (_, goal, task) = Fixture(assigned);
        Assert.Null(task.LastExecution);
        Assert.Null(task.LastDispatch);
        Assert.Null(task.LastProcess);
        Assert.Null(task.LastVerification);

        Assert.Equal(new[]
        {
            $"12345678 [{(assigned ? "Assigned" : "Pending")}] Developer: Render task details",
            $"Assigned agent: {(assigned ? task.AssignedAgentId!.Value : "unassigned")}",
            "Verification plan: none",
            "Task timeline:",
            $"  {Start.AddSeconds(1):u} TaskNote: First task event.",
            $"  {Start.AddSeconds(2):u} TaskNote: Newest task event."
        }, CaptureLines(() => TaskTextView.PrintTask(goal, task)));
    }

    [Fact]
    public void PrintTask_ExecutionAndDispatch_PreservesBlocksAndContextUsage()
    {
        var (kernel, goal, task) = Fixture(execution: true);
        var dispatchedAt = Start.AddMinutes(5);
        kernel.RecordTaskDispatch(goal.Id, task.Id, new TaskDispatchRecord(
            "local-worker", "run cmd", @"C:\work", dispatchedAt,
            ProviderName: "Ollama", ModelName: "qwen-local", PromptCharacterCount: 2048,
            ContextPackageReceipt: new WorkerContextPackageReceipt("test", [],
                ProviderUsageValue.Reported(90000), ProviderUsageValue.Reported(60000),
                ProviderUsageValue.Reported(2000), RenderedPromptBytes: 512, ModelInputTokenEstimate: 30000)));

        Assert.Equal(new[]
        {
            "12345678 [Running] Developer: Render task details",
            $"Assigned agent: {task.AssignedAgentId!.Value}",
            "Verification plan: none",
            "Last execution: Ollama/qwen-local by Local developer",
            "Stop reason: stop",
            "Usage: input=100 output=20 maxOutput=4000",
            "Prompt size: 1234 chars",
            "Output:",
            "done",
            "Last dispatch: worker=local-worker command=run cmd",
            "Dispatch model: Ollama/qwen-local complexity=unknown reasoning=default",
            "Prompt size: 2048 chars",
            "Context usage: input=90000, cached=60000, uncached=30000, output=2000, prompt-bytes=512, estimate=30000, harness-overhead=60000",
            @"Working directory: C:\work",
            $"Dispatched: {dispatchedAt:u}",
            "Task timeline:",
            $"  {Start.AddSeconds(1):u} TaskNote: First task event.",
            $"  {Start.AddSeconds(2):u} TaskNote: Newest task event.",
            $"  {dispatchedAt:u} TaskDispatchRecorded: Dispatched to local-worker using Ollama/qwen-local: run cmd"
        }, CaptureLines(() => TaskTextView.PrintTask(goal, task)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Refresh_WithoutRecords_PreservesPositionsAndOptionalNewestHistory(bool history)
    {
        var (_, goal, task) = Fixture();
        var lines = CaptureLines(() => TaskTextView.PrintRefreshDispatchResult(goal, task,
            new CliArgumentParser.RefreshDispatchOptions([], history, 1)));

        Assert.Equal(history ? 13 : 11, lines.Length);
        Assert.Equal($"Task: id=12345678 status=Assigned role=Developer assigned={task.AssignedAgentId!.Value}", lines[0]);
        Assert.Equal("Dispatch: none", lines[1]);
        Assert.Equal("Process: none", lines[2]);
        Assert.StartsWith("Dispatch state: ", lines[3]);
        Assert.StartsWith("Heartbeat: ", lines[4]);
        Assert.Equal("Exit evidence: none", lines[5]);
        Assert.Equal("Exit classification: none", lines[6]);
        Assert.Equal("Structured blockers: status=unknown value=none", lines[7]);
        Assert.Equal("Latest verification: none", lines[8]);
        Assert.StartsWith("Disposition: ", lines[9]);
        Assert.StartsWith("Next action: ", lines[10]);
        if (history)
        {
            Assert.Equal("Task timeline:", lines[11]);
            Assert.Equal($"  {Start.AddSeconds(2):u} TaskNote: Newest task event.", lines[12]);
        }
        else
        {
            Assert.DoesNotContain("Task timeline:", lines);
        }
    }

    [Fact]
    public void GoalTimeline_PreservesChronologicalGoalAndTaskLines()
    {
        var (_, goal, _) = Fixture();
        Assert.Equal(new[]
        {
            "Timeline:",
            $"  {Start:u} GoalCreated goal: Goal created.",
            $"  {Start.AddSeconds(1):u} TaskNote 12345678: First task event.",
            $"  {Start.AddSeconds(2):u} TaskNote 12345678: Newest task event."
        }, CaptureLines(() => TaskTimelineTextView.PrintTimeline(goal)));
    }

    [Fact]
    public void TaskTimeline_LimitOne_PreservesOnlyNewestEvent()
    {
        var (_, goal, task) = Fixture();
        Assert.Equal(new[] { $"  {Start.AddSeconds(2):u} TaskNote: Newest task event." },
            CaptureLines(() => TaskTimelineTextView.PrintTaskTimeline(goal, task, 1)));
    }

    [Fact]
    public void TaskTimeline_WithoutEvents_PreservesEmptyMessage()
    {
        var (_, goal, task) = Fixture(events: false);
        Assert.DoesNotContain(goal.Timeline, item => item.TaskId == task.Id);
        Assert.Equal(new[] { "  no task timeline events" },
            CaptureLines(() => TaskTimelineTextView.PrintTaskTimeline(goal, task)));
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("  ", "unknown")]
    [InlineData("a\n  b", "a b")]
    public void OneLine_PreservesFallbackAndWhitespace(string? value, string expected)
    {
        Assert.Equal(expected, TaskTimelineTextView.OneLine(value));
    }

    [Fact]
    public void OneLine_LongWord_PreservesExisting401CharacterTruncation()
    {
        var value = new string('x', 500);
        var actual = TaskTimelineTextView.OneLine(value);
        // HEAD takes 400 - 14 characters but appends a 15-character suffix.
        Assert.Equal(value[..386] + "... [truncated]", actual);
        Assert.Equal(401, actual.Length);
    }

    [Fact]
    public void Delegation_EmptyAndSingleAssignment_PreservesExactLines()
    {
        var (_, goal, task) = Fixture();
        Assert.Equal(new[] { "No pending tasks were delegated." }, CaptureLines(() =>
            TaskTextView.PrintDelegationPlan(goal, new DelegationPlan(goal.Id, []))));
        var agent = task.AssignedAgentId!;
        Assert.Equal(new[] { "Delegated tasks:", $"  task 1 -> {agent.Value} (Developer)" }, CaptureLines(() =>
            TaskTextView.PrintDelegationPlan(goal, new DelegationPlan(goal.Id,
                [new TaskAssignment(task.Id, agent, AgentRole.Developer)]))));
    }

    [Fact]
    public void PendingInput_EmptyKernel_PreservesExactMessage()
    {
        var kernel = new AgentOrchestratorKernel(new FixedClock(Start));
        Assert.Equal(new[] { "No pending human input." },
            CaptureLines(() => TaskTextView.PrintPendingHumanInput(kernel)));
    }

    [Theory]
    [InlineData(true, false, WorkTaskStatus.Completed, "completes the task")]
    [InlineData(false, false, WorkTaskStatus.Assigned, "returns the task for another round")]
    [InlineData(false, true, WorkTaskStatus.Running, "returns the task for another round")]
    public void PendingAndAnswer_ReportCompletionAndBothRestoreStatuses(
        bool verified, bool running, WorkTaskStatus expectedStatus, string prediction)
    {
        using var fixture = new AnswerFixture();
        var requestId = fixture.Request().Id;
        if (verified) fixture.Verify();
        if (running) fixture.RecordProcess(running: true);
        var request = fixture.GetRequest(requestId);
        Assert.Equal(verified, fixture.Task.LastVerification?.Succeeded == true);
        Assert.Equal(running, fixture.Task.LastProcess?.IsRunning == true);

        Assert.Equal(PendingLine(request, 1, prediction),
            Assert.Single(CaptureLines(() => TaskTextView.PrintPendingHumanInput(fixture.Kernel))));
        fixture.AssertPredictionThenAnswer(request, verified
            ? HumanInputAnswerEffect.CompletesTask
            : HumanInputAnswerEffect.ReturnsTaskForAnotherRound, expectedStatus);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingAndAnswer_CloseMatchingDuplicatesAndKeepIndependentWaits(bool independent)
    {
        using var fixture = new AnswerFixture();
        fixture.Request();
        fixture.Verify();
        var first = Assert.Single(fixture.Kernel.ExportSnapshot().HumanInputRequests);
        var duplicate = first with { Id = "duplicate-request", RequestedAt = first.RequestedAt.AddSeconds(1) };
        var other = first with
        {
            Id = "other-request", Question = "Which test is required?", QuestionFingerprint = null,
            RequestedAt = first.RequestedAt.AddSeconds(2)
        };
        fixture.LoadRequests(independent ? [first, duplicate, other] : [first, duplicate]);
        var requests = fixture.Kernel.GetPendingHumanInput(fixture.Goal.Id).OrderBy(item => item.RequestedAt).ToArray();
        Assert.Equal(independent ? 3 : 2, requests.Length);
        Assert.True(requests[0].HasSameIdentity(requests[1]));
        if (independent) Assert.False(requests[0].HasSameIdentity(requests[2]));

        var suffix = independent ? "task status WaitingForHuman" : "completes the task";
        var expected = requests.Take(2).Select(request => PendingLine(request, requests.Length, suffix));
        if (independent)
            expected = expected.Append(PendingLine(requests[2], requests.Length, "task status WaitingForHuman"));
        Assert.Equal(expected, CaptureLines(() => TaskTextView.PrintPendingHumanInput(fixture.Kernel)));

        fixture.AssertPredictionThenAnswer(requests[0],
            independent ? HumanInputAnswerEffect.TaskStatus : HumanInputAnswerEffect.CompletesTask,
            independent ? WorkTaskStatus.WaitingForHuman : WorkTaskStatus.Completed);
        Assert.True(requests[0].IsCompleted);
        Assert.True(requests[1].IsCompleted);
        Assert.Equal(requests[0].Id, requests[1].SupersededByRequestId);
        if (independent)
            Assert.Same(requests[2], Assert.Single(fixture.Kernel.GetPendingBlockingHumanInput(fixture.Goal.Id)));
        else
            Assert.Empty(fixture.Kernel.GetPendingBlockingHumanInput(fixture.Goal.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingAndAnswer_ReportNoTaskEffectForGoalAndNonblockingRequests(bool goalLevel)
    {
        using var fixture = new AnswerFixture();
        var request = goalLevel
            ? fixture.Kernel.RequestHumanInput(fixture.Goal.Id, null, "Which contract applies?")
            : fixture.Request(HumanWaitKind.ProspectiveAcceptanceEvidence);
        Assert.Equal(WorkTaskStatus.Assigned, fixture.Task.Status);
        var taskBefore = JsonSerializer.Serialize(fixture.TaskSnapshot);
        Assert.Equal(PendingLine(request, 1, "no task effect"),
            Assert.Single(CaptureLines(() => TaskTextView.PrintPendingHumanInput(fixture.Kernel))));

        fixture.AssertPredictionThenAnswer(request, HumanInputAnswerEffect.NoTaskEffect, WorkTaskStatus.Assigned);

        Assert.Equal(taskBefore, JsonSerializer.Serialize(fixture.TaskSnapshot));
    }

    [Fact]
    public void Answer_RejectedSubmissionPrintsNoOutcome()
    {
        using var fixture = new AnswerFixture();
        var request = fixture.Request();
        fixture.Kernel.SubmitHumanInput(request.Id, "Original answer.");

        var output = AsyncLocalConsoleRouter.Capture(() =>
        {
            var exception = Assert.Throws<InvalidOperationException>(() => fixture.Answer(request));
            Assert.Contains("has already been answered", exception.Message);
        });

        Assert.Empty(output);
        Assert.Equal("Original answer.", request.Answer);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Prediction_ObsoleteEvidenceAndSupersededBriefReturnPlanner(bool obsoleteEvidence)
    {
        using var fixture = new AnswerFixture(AgentRole.Planner);
        fixture.Revise();
        fixture.RecordDispatch(briefVersion: 1);
        fixture.RecordProcess(running: false);
        var requestId = fixture.Request().Id;
        fixture.Verify(batch: obsoleteEvidence ? PlannerEvidenceBatch() : null);
        var request = fixture.GetRequest(requestId);
        Assert.True(fixture.Task.LastVerification!.Succeeded);
        Assert.NotEqual(fixture.Goal.AuthoritativeBrief.Version, fixture.Task.LastDispatch!.BriefVersion);
        Assert.NotNull(fixture.Task.LastDispatch.BriefSnapshot);
        Assert.False(fixture.Task.LastProcess!.IsRunning);
        Assert.Equal(fixture.Task.LastDispatch.DispatchedAt, fixture.Task.LastVerification.DispatchAttemptAt);
        Assert.Equal(obsoleteEvidence, fixture.Task.LastVerification.PlannerEvidenceBatch?.Directives
            .Any(item => item.PlannerEvidence is not null) == true);

        fixture.AssertPredictionThenAnswer(request, HumanInputAnswerEffect.ReturnsTaskForAnotherRound, WorkTaskStatus.Assigned);

        var expectedNote = obsoleteEvidence
            ? "Historical Planner evidence retained; planning must run against the current brief."
            : "Planner must re-plan: dispatch brief v1 was superseded by current brief v2.";
        Assert.Contains(fixture.Goal.Timeline, item => item.Kind == ProgressKind.TaskNote && item.Message == expectedNote);
        Assert.Null(fixture.Task.LastVerification);
    }

    [Theory]
    [InlineData(false, WorkTaskStatus.Assigned)]
    [InlineData(true, WorkTaskStatus.Running)]
    public void Prediction_PremiseInvalidPlannerRerunsOrRestoresRunningProcess(bool running, WorkTaskStatus expectedStatus)
    {
        using var fixture = new AnswerFixture(AgentRole.Planner);
        fixture.RecordDispatch(briefVersion: 1);
        fixture.RecordProcess(running);
        var requestId = fixture.Request().Id;
        fixture.Verify(output: """
            WORKER_RESULT:
            blockers: premise-invalid - The original API was removed; src/Api.cs records its replacement.
            END_WORKER_RESULT
            """);
        var request = fixture.GetRequest(requestId);
        Assert.True(fixture.Task.LastVerification!.Succeeded);
        Assert.True(WorkerResultBlockers.TryFindPremiseInvalidEvidence(fixture.Task.LastVerification, out _));
        Assert.Equal(fixture.Goal.AuthoritativeBrief.Version, fixture.Task.LastDispatch!.BriefVersion);

        fixture.AssertPredictionThenAnswer(request, HumanInputAnswerEffect.ReturnsTaskForAnotherRound, expectedStatus);

        Assert.Equal(!running, fixture.Goal.Timeline.Any(item =>
            item.Message == "Planner reported a premise-invalid blocker; planning must rerun with the human answer."));
    }

    [Theory]
    [InlineData(false, WorkTaskStatus.WaitingForHuman, "task status WaitingForHuman")]
    [InlineData(true, WorkTaskStatus.Assigned, "returns the task for another round")]
    public void PendingAndAnswer_AmendmentHoldsOrReplansAfterRevision(
        bool revisionFirst, WorkTaskStatus expectedStatus, string prediction)
    {
        using var fixture = new AnswerFixture(AgentRole.Planner);
        var requestId = fixture.Kernel.RequestHumanInputDeduplicated(fixture.Goal.Id, fixture.Task.Id,
            "Revise criterion one?", kind: HumanWaitKind.PlannerBriefAmendment, amendmentCriterionIndex: 1).Request.Id;
        if (revisionFirst) fixture.Revise();
        fixture.Verify();
        var request = fixture.GetRequest(requestId);
        Assert.True(fixture.Task.LastVerification!.Succeeded);
        Assert.False(request.IsCompleted);
        Assert.Equal(revisionFirst ? 2 : 1, fixture.Goal.AuthoritativeBrief.Version);
        Assert.Equal(1, request.Amendment!.RequestedBriefVersion);

        Assert.Equal(PendingLine(request, 1, prediction),
            Assert.Single(CaptureLines(() => TaskTextView.PrintPendingHumanInput(fixture.Kernel))));
        fixture.AssertPredictionThenAnswer(request, revisionFirst
            ? HumanInputAnswerEffect.ReturnsTaskForAnotherRound
            : HumanInputAnswerEffect.TaskStatus, expectedStatus);

        Assert.Equal(revisionFirst, request.AmendmentReplanRouted);
        if (revisionFirst)
            Assert.Single(fixture.Goal.Timeline, item => item.Kind == ProgressKind.TaskRetried && item.TaskId == fixture.Task.Id);
        else
            Assert.Equal(request.Id, fixture.Kernel.GetPlannerBriefAmendmentRevisionPending(fixture.Goal.Id)?.RequestId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PendingAndAnswer_ReportFailedForRejectedAndHistoricalPlannerRounds(bool historical)
    {
        using var fixture = new AnswerFixture(AgentRole.Planner);
        var requestId = fixture.Kernel.RequestHumanInputDeduplicated(fixture.Goal.Id, fixture.Task.Id,
            "Which receipt applies?", kind: HumanWaitKind.PlannerPrerequisiteEvidence,
            plannerIdentity: new(fixture.Goal.Id.Value, fixture.Task.Id.Value, 1, 1, "receipt")).Request.Id;
        fixture.Verify(exitCode: historical ? 0 : 1,
            status: historical ? WorkTaskStatus.WaitingForHuman : WorkTaskStatus.Failed);
        if (historical) fixture.Revise();
        var request = fixture.GetRequest(requestId);
        Assert.Equal(historical ? WorkTaskStatus.WaitingForHuman : WorkTaskStatus.Failed, fixture.Task.Status);
        Assert.Equal(historical, fixture.Task.LastVerification!.Succeeded);
        Assert.Equal(!historical, request.IsCurrentForBrief(fixture.Goal.AuthoritativeBrief.Version));

        Assert.Equal(PendingLine(request, 1, "task status Failed"),
            Assert.Single(CaptureLines(() => TaskTextView.PrintPendingHumanInput(fixture.Kernel))));
        fixture.AssertPredictionThenAnswer(request, HumanInputAnswerEffect.TaskStatus, WorkTaskStatus.Failed);
        Assert.Contains(fixture.Goal.Timeline, item => historical
            ? item.Message.StartsWith("Historical Planner answer retained for brief v1", StringComparison.Ordinal)
            : item.Message.StartsWith("Planner answer recorded; the rejected Planner round remains failed", StringComparison.Ordinal));
    }

    private static string PendingLine(HumanInputRequest request, int count, string prediction)
    {
        var taskId = request.TaskId is null ? "goal" : request.TaskId.Value[..8];
        return $"{request.Id.Value[..8]} goal={request.GoalId.Value[..8]} task={taskId} " +
            $"requests={count} open / {count} total: {request.Question} | answer: {prediction}";
    }

    private static HumanInputDirectiveParseResult PlannerEvidenceBatch()
    {
        var payload = new PlannerEvidencePayload(1, "receipt", "post-implementation", "operator",
            "Confirm the receipt.", "Completion depends on it.", null, null, null);
        return new(new HumanInputDirective(payload.Needed, "receipt", Kind: HumanWaitKind.ProspectiveAcceptanceEvidence,
            EvidenceOwner: payload.Owner, PlannerEvidence: payload), null);
    }

    private sealed class AnswerFixture : IDisposable
    {
        private static readonly FixedClock Clock = new(Start.AddMinutes(5));
        private readonly DirectoryInfo _root = Directory.CreateTempSubdirectory("mcg-answer-outcome-");
        internal AgentOrchestratorKernel Kernel { get; private set; } = new(Clock);
        private readonly GoalId _goalId = new("answer01");
        internal Goal Goal => Kernel.GetGoal(_goalId);
        internal TaskSpec Task => Goal.Tasks[0];
        internal TaskSnapshot TaskSnapshot => Kernel.ExportSnapshot().Goals.Single().Tasks[0];

        internal AnswerFixture(AgentRole role = AgentRole.Developer)
        {
            Kernel.CreateGoal(_goalId, "Resolve the human input",
                [new TaskSpec(new TaskId("12345678-answer"), "Answering task", role),
                 new TaskSpec(new TaskId("87654321-next"), "Downstream task", AgentRole.Tester)]);
            Kernel.ActivateGoal(_goalId, AgentCatalog.Default().Agents);
        }

        internal HumanInputRequest Request(HumanWaitKind kind = HumanWaitKind.SpecClarification) =>
            Kernel.RequestHumanInput(_goalId, Task.Id, "Which contract applies?", kind);

        internal HumanInputRequest GetRequest(HumanInputRequestId id) =>
            Assert.Single(Kernel.HumanInputRequests, request => request.Id == id);

        internal void Revise() => Kernel.ReviseGoalBrief(_goalId, "Use the revised contract.");

        internal void RecordDispatch(int briefVersion) =>
            UpdateTask(task => task with
            {
                LastDispatch = new("planner", "worker", _root.FullName, Start,
                    BriefVersion: briefVersion, BriefSnapshot: "Resolve the human input")
            });

        internal void RecordProcess(bool running) =>
            UpdateTask(task => task with
            {
                LastProcess = new(101, "worker", _root.FullName, "stdout", "stderr", "exit",
                    Start.AddSeconds(1), running ? null : Start.AddSeconds(2), running ? null : 0)
            });

        internal void Verify(int exitCode = 0, string output = "Verified.",
            HumanInputDirectiveParseResult? batch = null, WorkTaskStatus status = WorkTaskStatus.WaitingForHuman)
        {
            var verification = new TaskVerificationSnapshot("verify", _root.FullName, exitCode, output, "", Start.AddSeconds(3),
                AuthoritativeStandardOutput: output, AuthoritativeStandardError: "",
                DispatchAttemptAt: Task.LastDispatch?.DispatchedAt, PlannerEvidenceBatch: batch);
            UpdateTask(task => task with { Status = status, LastVerification = verification, VerificationHistory = [verification] });
        }

        private void UpdateTask(Func<TaskSnapshot, TaskSnapshot> update)
        {
            var snapshot = Kernel.ExportSnapshot();
            var goal = Assert.Single(snapshot.Goals);
            Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
            {
                Goals = [goal with { Tasks = [update(goal.Tasks[0]), .. goal.Tasks.Skip(1)] }]
            }, Clock);
        }

        internal void LoadRequests(IReadOnlyList<HumanInputRequestSnapshot> requests) =>
            Kernel = AgentOrchestratorKernel.FromSnapshot(Kernel.ExportSnapshot() with { HumanInputRequests = requests }, Clock);

        internal void Answer(HumanInputRequest request)
        {
            var context = new CliExecutionContext(Kernel, OrchestratorWorkspace.ForDirectory(_root.FullName),
                new InMemoryModelProviderRegistry([]), AgentCatalog.Default().Agents, WorkerProfileCatalog.Default(), Goal);
            Assert.True(CliCommandHandlers.Execute(["answer", request.Id.Value, "Use the authoritative contract."], context));
        }

        internal void AssertPredictionThenAnswer(HumanInputRequest request, HumanInputAnswerEffect effect, WorkTaskStatus status)
        {
            Kernel.SetEventWriter(new GoalLifecycleEventWriter(_root.FullName, Clock,
                sessionRetentionOptions: DispatchProviderSessionRetentionOptions.Default));
            var eventFile = Path.Combine(_root.FullName, $"{_goalId.Value}.jsonl");
            var before = JsonSerializer.Serialize(Kernel.ExportSnapshot());
            var timelineBefore = Goal.Timeline.ToArray();
            var predicted = Kernel.PredictHumanInputAnswerOutcome(request.Id);
            Assert.Equal(new HumanInputAnswerOutcome(effect, effect == HumanInputAnswerEffect.NoTaskEffect ? null : status), predicted);
            Assert.Equal(predicted, Kernel.PredictHumanInputAnswerOutcome(request.Id));
            Assert.Equal(before, JsonSerializer.Serialize(Kernel.ExportSnapshot()));
            Assert.Equal(timelineBefore, Goal.Timeline.ToArray());
            Assert.False(File.Exists(eventFile));

            var beforeStatus = Task.Status;
            var output = CaptureLines(() => Answer(request));
            var goalLines = CaptureLines(() => GoalTextView.PrintGoal(Goal));
            if (request.TaskId is null)
                Assert.Equal(goalLines, output);
            else
                Assert.Equal(goalLines.Prepend($"Task 12345678 after answer: {status} (was {beforeStatus})"), output);
            Assert.Equal(status, Task.Status);
            Assert.True(request.IsCompleted);
            Assert.NotEmpty(File.ReadAllLines(eventFile));
        }

        public void Dispose() => _root.Delete(recursive: true);
    }

    [Fact]
    public void ExtractedOwners_LeaveNoPartialFilesOrConsoleViewsForwarders()
    {
        var cliRoot = Path.Combine(VerifiedRepositoryRoot.Find(), "src", "Mcg.AgentOrchestrator.App", "Cli");
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.TaskDetails.cs")));
        Assert.False(File.Exists(Path.Combine(cliRoot, "ConsoleViews.ContextUsage.cs")));
        foreach (var owner in new[] { "TaskTextView", "TaskTimelineTextView" })
        {
            var source = File.ReadAllText(Path.Combine(cliRoot, owner + ".cs"));
            Assert.Contains("internal static class " + owner, source);
            Assert.DoesNotMatch(@"\bpartial\s+class\b", source);
        }

    }

    [Fact]
    public void ConsoleViewsRatchet_UsesReducedAtOrUnderBudget()
    {
        var ceiling = Assert.Single(SourceSizeRatchet.SeededClassCeilings,
            row => row.ClassName == "ConsoleViews");
        Assert.True(ceiling.MaximumTotalLineCount <= 2462);
        Assert.True(ceiling.MaximumPartialFileCount <= 33);
        Assert.Empty(SourceSizeRatchet.EvaluateClasses(VerifiedRepositoryRoot.Find(), [ceiling]));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Task) Fixture(
        bool assigned = true, bool execution = false, bool events = true)
    {
        var clock = new FixedClock(Start);
        var kernel = new AgentOrchestratorKernel(clock);
        var goal = kernel.CreateGoal(new GoalId("goal0001"), "Characterize task output",
            [new TaskSpec(new TaskId("12345678-task"), "Render task details", AgentRole.Developer)]);
        if (assigned)
            kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
        var snapshot = kernel.ExportSnapshot();
        var goalSnapshot = Assert.Single(snapshot.Goals);
        var taskSnapshot = Assert.Single(goalSnapshot.Tasks);
        if (execution)
        {
            taskSnapshot = taskSnapshot with
            {
                LastExecution = new TaskExecutionSnapshot(taskSnapshot.AssignedAgentId!, "Local developer",
                    "Ollama", "qwen-local", "done", "stop", 100, 20, Start.AddSeconds(3),
                    MaxOutputTokens: 4000, PromptCharacterCount: 1234)
            };
        }
        // Deliberately store events out of order so sorting and TakeLast are observable.
        ProgressEventSnapshot[] timeline = events
            ? [new(goalSnapshot.Id, taskSnapshot.Id, ProgressKind.TaskNote, "Newest task event.", Start.AddSeconds(2)),
               new(goalSnapshot.Id, null, ProgressKind.GoalCreated, "Goal created.", Start),
               new(goalSnapshot.Id, taskSnapshot.Id, ProgressKind.TaskNote, "First task event.", Start.AddSeconds(1))]
            : [new(goalSnapshot.Id, null, ProgressKind.GoalCreated, "Goal created.", Start)];
        kernel = AgentOrchestratorKernel.FromSnapshot(snapshot with
        {
            Goals = [goalSnapshot with { Tasks = [taskSnapshot], Timeline = timeline }]
        }, new FixedClock(Start.AddMinutes(5)));
        goal = Assert.Single(kernel.Goals);
        return (kernel, goal, Assert.Single(goal.Tasks));
    }

    private static string[] CaptureLines(Action action) =>
        AsyncLocalConsoleRouter.Capture(action).TrimEnd('\r', '\n').Split(Environment.NewLine);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow => now;
    }
}
