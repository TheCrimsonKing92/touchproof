using Mcg.AgentOrchestrator.Core;

// Parallel-safe: each case uses its own in-memory kernel and clock.
public sealed class PlannerSupersededBriefReplanTests
{
    private const string Clarification = "Which contract should planning use?";
    private const string Answer = "Use the authoritative brief.";
    private const string PremiseInvalid = "premise-invalid - the original API was removed; src/Api.cs records its replacement";

    [Fact]
    public void AnswerRequeuesSupersededClarificationWithoutEvidenceDirectives()
    {
        var scenario = new Scenario();
        scenario.Dispatch(scenario.Planner);
        scenario.ReviseBrief();
        // The v1 dispatch reaches its clarification after the revision, so only answering can requeue it.
        var request = scenario.WaitForClarification(scenario.Planner, question: Clarification);
        var verification = Assert.IsType<TaskVerificationRecord>(scenario.Planner.LastVerification);
        Assert.True(verification.Succeeded);
        Assert.Empty(Assert.IsType<HumanInputDirectiveParseResult>(verification.PlannerEvidenceBatch).Directives);
        Assert.Equal(1, scenario.Planner.LastDispatch!.BriefVersion);

        scenario.Kernel.SubmitHumanInput(request.Id, Answer);

        Assert.True(request.IsCompleted);
        AssertRequeued(scenario);
        AssertSupersededNote(scenario);
    }

    [Fact]
    public void AnswerRequeuesPremiseInvalidPlannerOnCurrentBrief()
    {
        var scenario = new Scenario();
        scenario.Dispatch(scenario.Planner);
        var request = scenario.WaitForClarification(scenario.Planner, blockers: PremiseInvalid);
        Assert.Equal(scenario.Goal.AuthoritativeBrief.Version, scenario.Planner.LastDispatch!.BriefVersion);
        Assert.True(WorkerResultBlockers.TryFindPremiseInvalidEvidence(scenario.Planner.LastVerification, out _));

        scenario.Kernel.SubmitHumanInput(request.Id, Answer);

        Assert.True(request.IsCompleted);
        AssertRequeued(scenario);
    }

    [Fact]
    public void RevisionRequeuesSupersededWaitingPlannerAndKeepsCurrentRoundWaiting()
    {
        var scenario = new Scenario(AgentRole.Planner);
        var currentPlanner = scenario.OtherTask;
        scenario.Dispatch(scenario.Planner);
        var staleRequest = scenario.WaitForClarification(scenario.Planner, question: Clarification);
        scenario.Dispatch(currentPlanner);
        var currentRequest = scenario.WaitForClarification(currentPlanner, question: "Which constraint applies?");
        var currentVerification = currentPlanner.LastVerification;
        // Seed a control with the dispatch version that will be authoritative after revision.
        currentPlanner.LastDispatch!.BriefVersion = scenario.Goal.AuthoritativeBrief.Version + 1;

        var revision = scenario.ReviseBrief();

        AssertRequeued(scenario);
        AssertSupersededNote(scenario);
        Assert.False(staleRequest.IsCompleted);
        Assert.Contains(scenario.Planner.Id, revision.NotYetStartedTaskIds);
        Assert.DoesNotContain(scenario.Planner.Id, revision.InFlightTaskIds);
        Assert.Equal(revision.AuthoritativeVersion.Version, currentPlanner.LastDispatch.BriefVersion);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, currentPlanner.Status);
        Assert.NotNull(currentVerification);
        Assert.Same(currentVerification, currentPlanner.LastVerification);
        Assert.False(currentRequest.IsCompleted);
        Assert.Contains(currentPlanner.Id, revision.InFlightTaskIds);
    }

    [Theory]
    [InlineData(WorkTaskStatus.Pending)]
    [InlineData(WorkTaskStatus.Assigned)]
    [InlineData(WorkTaskStatus.Running)]
    [InlineData(WorkTaskStatus.WaitingForHuman)]
    public void StaleSpecificationRefusesDeveloperDispatchWithUnfinishedDispatchedPlanner(WorkTaskStatus status)
    {
        var scenario = new Scenario();
        scenario.Dispatch(scenario.Planner);
        scenario.ReviseBrief();
        switch (status)
        {
            case WorkTaskStatus.Pending:
                scenario.Planner.SetStatus(WorkTaskStatus.Pending);
                break;
            case WorkTaskStatus.Assigned:
                scenario.Kernel.ReportTaskProgress(scenario.Goal.Id, scenario.Planner.Id, WorkTaskStatus.Failed, "Plan needs a retry.");
                scenario.Kernel.RetryTask(scenario.Goal.Id, scenario.Planner.Id, "Retry planning.");
                Assert.Null(scenario.Planner.LastDispatch);
                break;
            case WorkTaskStatus.WaitingForHuman:
                scenario.WaitForClarification(scenario.Planner, question: Clarification);
                break;
        }
        Assert.Equal(status, scenario.Planner.Status);
        Assert.Single(scenario.Planner.DispatchHistory);

        AssertDispatchRefused(scenario);
    }

    [Theory]
    [InlineData(AgentRole.Tester)]
    [InlineData(AgentRole.Reviewer)]
    public void StaleSpecificationAlsoRefusesTesterAndReviewerDispatch(AgentRole role)
    {
        var scenario = new Scenario(role);
        scenario.Dispatch(scenario.Planner);
        scenario.ReviseBrief();

        AssertDispatchRefused(scenario);
    }

    [Fact]
    public void CurrentSpecificationAllowsDeveloperDispatchWithUnfinishedPlanner()
    {
        var scenario = new Scenario();
        scenario.Dispatch(scenario.Planner);
        scenario.ReviseBrief();
        scenario.RecordSpecification("Use the revised contract.");
        Assert.Equal(2, scenario.Goal.AuthoritativeRefinedSpecVersion!.BriefVersion);
        Assert.Equal(WorkTaskStatus.Running, scenario.Planner.Status);

        AssertDispatchRecordedOnCurrentBrief(scenario);
    }

    [Fact]
    public void StaleSpecificationAllowsDeveloperDispatchWhenPlannerWasNeverDispatched()
    {
        var scenario = new Scenario();
        scenario.ReviseBrief();
        Assert.Equal(WorkTaskStatus.Assigned, scenario.Planner.Status);
        Assert.Empty(scenario.Planner.DispatchHistory);

        AssertDispatchRecordedOnCurrentBrief(scenario);
    }

    [Theory]
    [InlineData(WorkTaskStatus.Completed)]
    [InlineData(WorkTaskStatus.Failed)]
    [InlineData(WorkTaskStatus.Cancelled)]
    public void StaleSpecificationAllowsDeveloperDispatchWhenDispatchedPlannerIsTerminal(WorkTaskStatus status)
    {
        var scenario = new Scenario();
        scenario.Dispatch(scenario.Planner);
        scenario.Kernel.ReportTaskProgress(scenario.Goal.Id, scenario.Planner.Id, status, "Planning round ended.");
        scenario.ReviseBrief();
        Assert.Equal(status, scenario.Planner.Status);
        Assert.Equal(1, scenario.Planner.LastDispatch!.BriefVersion);

        AssertDispatchRecordedOnCurrentBrief(scenario);
    }

    [Theory]
    [InlineData(AgentRole.Planner)]
    [InlineData(AgentRole.Researcher)]
    public void StaleSpecificationAllowsPlanningAndResearchDispatch(AgentRole role)
    {
        var scenario = new Scenario(role);
        scenario.Dispatch(scenario.Planner);
        scenario.ReviseBrief();
        Assert.Equal(WorkTaskStatus.Running, scenario.Planner.Status);

        AssertDispatchRecordedOnCurrentBrief(scenario);
    }

    [Fact]
    public void MissingSpecificationAllowsDeveloperDispatchWithUnfinishedPlanner()
    {
        var scenario = new Scenario(withSpecification: false);
        scenario.Dispatch(scenario.Planner);
        scenario.ReviseBrief();
        Assert.Null(scenario.Goal.AuthoritativeRefinedSpecVersion);
        Assert.Equal(WorkTaskStatus.Running, scenario.Planner.Status);

        AssertDispatchRecordedOnCurrentBrief(scenario);
    }

    [Fact]
    public void PremiseInvalidReplayLeavesPlannerAssignedAndDeveloperUndispatched()
    {
        var scenario = ReplayPremiseInvalidRound();

        AssertRequeued(scenario);
        Assert.Equal(WorkTaskStatus.Assigned, scenario.OtherTask.Status);
        Assert.Empty(scenario.OtherTask.DispatchHistory);
        AssertDispatchRefused(scenario);
    }

    [Fact]
    public void PremiseInvalidReplayRefusesDeveloperUntilPlannerCompletesOnRevisedBrief()
    {
        var scenario = ReplayPremiseInvalidRound();
        AssertDispatchRefused(scenario);

        scenario.Dispatch(scenario.Planner);
        Assert.Equal(2, scenario.Planner.LastDispatch!.BriefVersion);
        AssertDispatchRefused(scenario);
        scenario.RecordPlannerResult(scenario.Planner);

        Assert.Equal(WorkTaskStatus.Completed, scenario.Planner.Status);
        Assert.Equal(1, scenario.Goal.AuthoritativeRefinedSpecVersion!.BriefVersion);
        AssertDispatchRecordedOnCurrentBrief(scenario);
    }

    private static Scenario ReplayPremiseInvalidRound()
    {
        var scenario = new Scenario();
        scenario.Dispatch(scenario.Planner);
        Assert.Equal(1, scenario.Planner.LastDispatch!.BriefVersion);
        var request = scenario.WaitForClarification(scenario.Planner, blockers: PremiseInvalid);
        Assert.StartsWith("Planner reported premise-invalid:", request.Question, StringComparison.Ordinal);
        scenario.ReviseBrief();
        Assert.Contains(request, scenario.Kernel.GetPendingHumanInput(scenario.Goal.Id));
        scenario.Kernel.SubmitHumanInput(request.Id, Answer);
        Assert.True(request.IsCompleted);
        return scenario;
    }

    private static void AssertRequeued(Scenario scenario)
    {
        Assert.Equal(WorkTaskStatus.Assigned, scenario.Planner.Status);
        Assert.Null(scenario.Planner.LastVerification);
        Assert.Single(scenario.Planner.VerificationHistory);
        Assert.DoesNotContain(scenario.Goal.Timeline, entry =>
            entry.TaskId == scenario.Planner.Id && entry.Kind == ProgressKind.TaskCompleted);
    }

    private static void AssertSupersededNote(Scenario scenario) =>
        Assert.Contains(scenario.Goal.Timeline, entry => entry.TaskId == scenario.Planner.Id &&
            entry.Kind == ProgressKind.TaskNote && entry.Message.Contains("dispatch brief v1", StringComparison.Ordinal) &&
            entry.Message.Contains("current brief v2", StringComparison.Ordinal));

    private static void AssertDispatchRefused(Scenario scenario)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => scenario.Dispatch(scenario.OtherTask));
        Assert.Contains("specification brief v1", exception.Message, StringComparison.Ordinal);
        Assert.Contains("current brief v2", exception.Message, StringComparison.Ordinal);
        Assert.Contains(scenario.Planner.Id.Value, exception.Message, StringComparison.Ordinal);
        Assert.Equal(WorkTaskStatus.Assigned, scenario.OtherTask.Status);
        Assert.Null(scenario.OtherTask.LastDispatch);
        Assert.Empty(scenario.OtherTask.DispatchHistory);
    }

    private static void AssertDispatchRecordedOnCurrentBrief(Scenario scenario)
    {
        scenario.Dispatch(scenario.OtherTask);
        Assert.Equal(WorkTaskStatus.Running, scenario.OtherTask.Status);
        Assert.Equal(2, Assert.Single(scenario.OtherTask.DispatchHistory).BriefVersion);
    }

    private sealed class Scenario
    {
        private readonly FakeClock _clock = new();

        public Scenario(AgentRole otherRole = AgentRole.Developer, bool withSpecification = true)
        {
            OtherTask = new(TaskId.New(), $"Perform {otherRole} work", otherRole);
            Kernel = new(_clock);
            Goal = Kernel.CreateGoal("Original brief", [Planner, OtherTask]);
            if (withSpecification)
                RecordSpecification("Use the original contract.");
            Kernel.ActivateGoal(Goal.Id, DefaultAgents());
        }

        public AgentOrchestratorKernel Kernel { get; }
        public Goal Goal { get; }
        public TaskSpec Planner { get; } = new(TaskId.New(), "Plan the work", AgentRole.Planner);
        public TaskSpec OtherTask { get; }

        public GoalBriefRevisionResult ReviseBrief()
        {
            _clock.Advance();
            var revision = Kernel.ReviseGoalBrief(Goal.Id, "Revised brief");
            Assert.Equal(2, Goal.AuthoritativeBrief.Version);
            if (Goal.AuthoritativeRefinedSpecVersion is { } specification)
                Assert.Equal(1, specification.BriefVersion);
            return revision;
        }

        public void RecordSpecification(string contract) =>
            Kernel.RecordGoalRefinement(Goal.Id, new RefinedSpec(contract, [], VerificationClass.TestVerifiable, [], []));

        public void Dispatch(TaskSpec task)
        {
            _clock.Advance();
            Kernel.RecordTaskDispatch(Goal.Id, task.Id,
                new TaskDispatchRecord("fixture", "inspect", "C:\\fixture", _clock.UtcNow));
        }

        public HumanInputRequest WaitForClarification(TaskSpec planner, string? question = null, string blockers = "none")
        {
            RecordPlannerResult(planner, question, blockers);
            Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
            var request = Assert.Single(Kernel.GetPendingHumanInput(Goal.Id), item => item.TaskId == planner.Id);
            Assert.Equal(HumanWaitKind.SpecClarification, request.Kind);
            Assert.Null(request.PlannerIdentity);
            return request;
        }

        public void RecordPlannerResult(TaskSpec planner, string? question = null, string blockers = "none")
        {
            var dispatch = Assert.IsType<TaskDispatchRecord>(planner.LastDispatch);
            var output = $$"""
                WORKER_RESULT:
                files: none
                commands: inspect
                tests: not-run - Planner
                commit: none
                blockers: {{blockers}}
                model_fit: fixture/model - adequate
                skills: none
                confidence: high
                END_WORKER_RESULT
                """;
            _clock.Advance();
            Kernel.RecordDispatchExecutionResult(Goal.Id, planner.Id,
                new TaskVerificationRecord(dispatch.Command, dispatch.WorkingDirectory, 0, output, "", _clock.UtcNow,
                    WorkerResultPresent: true, HumanInputQuestion: question, DispatchAttemptAt: dispatch.DispatchedAt));
        }
    }
}
