using System.Text.Json;
using Mcg.AgentOrchestrator.App.Cli;
using Mcg.AgentOrchestrator.App.Orchestration;
using Mcg.AgentOrchestrator.Core;
using Mcg.AgentOrchestrator.Core.Conductor;
using Mcg.AgentOrchestrator.Infrastructure;

// Parallel-safe: kernels, output files, and SQLite stores are owned by each fixture.
public sealed class PlannerBriefAmendmentAnswerRoutingTests : WorkerDispatchTestSupport
{
    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "CrossTick")]
    public void ApprovedAnswer_ThreeTicksHoldWithTypedReasonAcrossReload(bool blocker)
    {
        using var f = new Fixture(blocker);
        f.Kernel.SubmitHumanInput(f.Request.Id, "Accept the proposed text.");

        for (var tick = 0; tick < 3; tick++)
        {
            f.Kernel = AgentOrchestratorKernel.FromSnapshot(f.Kernel.ExportSnapshot());
            var held = Assert.IsType<ConductorAdvanceOutcome.Held>(f.Tick().Outcome);
            var reason = Assert.IsType<PlannerBriefAmendmentRevisionPendingHold>(held.AmendmentRevisionPending);
            Assert.Equal(f.Request.Id, reason.RequestId);
            Assert.Equal(1, reason.CriterionIndex);
            Assert.Equal(1, reason.RequestedBriefVersion);
            Assert.Equal(reason, f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
            Assert.Equal(reason.Identity, held.StableIdentity);
            Assert.Contains(reason.RequestId.Value, held.Reason);
            Assert.Contains("criterion=1", held.Reason);
            Assert.Equal(WorkTaskStatus.WaitingForHuman, f.Planner.Status);
            Assert.Equal(0, f.Dispatches);
            Assert.Equal(0, f.Escalations);
        }
        Assert.DoesNotContain(f.Goal.Timeline, item => item.Kind is ProgressKind.TaskCompleted or ProgressKind.TaskRetried);
        var hold = f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id)!;
        var holds = PlannerBriefAmendmentHoldAttentionView.Holds(f.Kernel, [f.Goal]);
        Assert.Equal(hold, Assert.Single(holds));
        using var attention = JsonDocument.Parse(AttentionShowAgentReadView.Render(f.Goal.Id.Value, [], [], _ => null,
            goals: [f.Goal], amendmentHolds: holds));
        var row = attention.RootElement.GetProperty("payload").GetProperty("amendmentRevisionPending")[0];
        Assert.Equal(PlannerBriefAmendmentRevisionPendingHold.State, row.GetProperty("state").GetString());
        Assert.Equal(hold.Render(), row.GetProperty("detail").GetString());
        Assert.Equal(hold.CriterionIndex, row.GetProperty("hold").GetProperty("criterionIndex").GetInt32());
        using var text = new StringWriter();
        Assert.True(PlannerBriefAmendmentHoldAttentionView.Print(f.Kernel, [f.Goal], text));
        Assert.Contains(hold.Render(), text.ToString());
    }

    [Theory(Timeout = TestHangGuard.WholeTestMilliseconds)]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "CrossTick")]
    public void BriefRevision_BeforeOrAfterAnswerReplansExactlyOnce(bool revisionFirst)
    {
        using var f = new Fixture();
        if (revisionFirst) f.Revise();
        f.Kernel.SubmitHumanInput(f.Request.Id, "Accept the proposed text.");
        if (!revisionFirst)
        {
            for (var tick = 0; tick < 3; tick++) Assert.NotNull(Assert.IsType<ConductorAdvanceOutcome.Held>(f.Tick().Outcome).AmendmentRevisionPending);
            f.Revise();
            Assert.Empty(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        }

        f.Tick();

        Assert.Null(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
        Assert.Equal(2, f.Goal.AuthoritativeBrief.Version);
        Assert.Equal(WorkTaskStatus.Assigned, f.Planner.Status);
        Assert.Null(f.Planner.LastDispatch);
        Assert.Null(f.Planner.LastVerification);
        var retry = Assert.Single(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        Assert.Contains(f.Request.Id.Value, retry.Message);
        Assert.Contains("criterion=1", retry.Message);
        Assert.Contains("current brief v2", retry.Message);
        Assert.True(f.Request.AmendmentReplanRouted);
        Assert.True(f.Dispatches > 0);
        Assert.Equal(0, f.Escalations);
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(f.Kernel.ExportSnapshot());
        f.Tick();
        Assert.Single(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
    }

    [Fact]
    public void TypedDecline_ReplansUnchangedBriefWithCompleteAnswer()
    {
        using var f = new Fixture();
        const string answer = "Decline this amendment.\nPreserve the regression and implement the current criterion.";
        f.Kernel.SubmitHumanInput(f.Request.Id, answer, amendmentDisposition: PlannerBriefAmendmentDisposition.Decline);

        Assert.Equal(1, f.Goal.AuthoritativeBrief.Version);
        Assert.Equal(WorkTaskStatus.Assigned, f.Planner.Status);
        Assert.Null(f.Planner.LastDispatch);
        Assert.Null(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
        Assert.Equal(PlannerBriefAmendmentDisposition.Decline, f.Request.AuthoritativeAnswer!.AmendmentDisposition);
        var retry = Assert.Single(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        Assert.Contains(answer, retry.Message);
        Assert.Contains(f.Request.Id.Value, retry.Message);
        Assert.Contains(answer, f.Kernel.BuildTaskBrief(f.Goal.Id, f.Planner.Id).Content);
        Assert.Contains(f.Planner.CriterionRetryFeedback, feedback => feedback.Contains(answer, StringComparison.Ordinal));
    }

    [Fact]
    public void TypedDecline_OnOrdinaryRequestIsRejectedWithoutCompletion()
    {
        using var f = new Fixture();
        var request = f.Kernel.RequestHumanInput(f.Goal.Id, f.Planner.Id, "Ordinary clarification");
        var error = Assert.Throws<ArgumentException>(() => f.Kernel.SubmitHumanInput(request.Id, "Decline",
            amendmentDisposition: PlannerBriefAmendmentDisposition.Decline));
        Assert.Contains("requires a request carrying a Planner brief amendment payload", error.Message);
        Assert.False(request.IsCompleted);
        Assert.Empty(request.AnswerHistory);
    }

    [Fact]
    public void LateDecline_SupersedesHeldAnswerAndAllowsFreshRequest()
    {
        using var f = new Fixture();
        f.Kernel.SubmitHumanInput(f.Request.Id, "Accept the proposed text.");
        Assert.NotNull(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
        f.Kernel.SupersedeHumanInput(f.Goal.Id, f.Request.Id, "Keep the current brief.", HumanInputAnswerOrigin.Operator,
            PlannerBriefAmendmentDisposition.Decline);
        Assert.Equal(WorkTaskStatus.Assigned, f.Planner.Status);
        Assert.True(f.Request.AmendmentReplanRouted);
        Assert.Equal(2, f.Request.AnswerHistory.Count);
        Assert.Null(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));

        var fresh = f.Kernel.RequestHumanInputDeduplicated(f.Goal.Id, f.Planner.Id, f.Request.Question,
            kind: HumanWaitKind.PlannerBriefAmendment, questionFingerprint: f.Request.QuestionFingerprint,
            blockerFingerprint: f.Request.BlockerFingerprint, amendmentCriterionIndex: 1);
        Assert.True(fresh.WasCreated);
        Assert.NotEqual(f.Request.Id, fresh.Request.Id);
        Assert.False(fresh.Request.AmendmentReplanRouted);
    }

    [Fact]
    public async Task SqliteMerge_PreservesAnsweredHoldAndLateDeclineRouting()
    {
        using var f = new Fixture();
        var repository = CreateMigratedStateRepository(Path.Combine(f.Root, "state.db"));
        await repository.SaveAsync(f.Kernel);
        var baseline = f.Kernel.ExportSnapshot();
        f.Kernel.SubmitHumanInput(f.Request.Id, "Accept the proposed text.");
        await MergeAfterPeerWrite("peer-one");
        var hold = f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id);
        Assert.NotNull(hold);
        Assert.Equal(1, f.Request.Amendment!.RequestedBriefVersion);
        baseline = f.Kernel.ExportSnapshot();
        f.Kernel.SupersedeHumanInput(f.Goal.Id, f.Request.Id, "Declined after reconsideration.", HumanInputAnswerOrigin.Operator,
            PlannerBriefAmendmentDisposition.Decline);
        await MergeAfterPeerWrite("peer-two");
        Assert.Equal(PlannerBriefAmendmentDisposition.Decline, f.Request.AuthoritativeAnswer!.AmendmentDisposition);
        Assert.True(f.Request.AmendmentReplanRouted);
        Assert.Equal(WorkTaskStatus.Assigned, f.Planner.Status);
        Assert.Null(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
        Assert.Single(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));

        async Task MergeAfterPeerWrite(string peer)
        {
            var other = await repository.LoadAsync();
            other.SetGoalSourceBacklogItemId(f.Goal.Id, peer);
            await repository.SaveAsync(other);
            var current = f.Kernel.ExportSnapshot();
            var result = Assert.Single(await repository.SaveGoalSnapshotsWithMergeAsync([
                new GoalSnapshotSaveRequest(baseline.Goals.Single(), current.Goals.Single(), current.HumanInputRequests)]));
            Assert.Equal(GoalSnapshotSaveDisposition.Merged, result.Disposition);
            f.Kernel = await repository.LoadAsync();
        }
    }

    [Fact]
    public async Task ConductorStatus_ProjectsSameHoldAsAttention()
    {
        using var f = new Fixture();
        var workspace = OrchestratorWorkspace.ForDirectory(f.Root);
        var repository = CreateMigratedStateRepository(workspace.SqliteStatePath);
        f.Kernel.SubmitHumanInput(f.Request.Id, "Accept the proposed text.");
        await repository.SaveAsync(f.Kernel);

        var snapshot = ConductorStatusSnapshotReader.Read(workspace, null, DateTimeOffset.UtcNow, mainCommit: () => null);

        var hold = Assert.Single(snapshot.Goals.NonTerminal!).AmendmentRevisionPending;
        Assert.Equal(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id), hold);
        Assert.Equal(Assert.Single(PlannerBriefAmendmentHoldAttentionView.Holds(f.Kernel, [f.Goal])), hold);
        using var output = new StringWriter();
        CliConductorStatusReader.Print(snapshot, output);
        Assert.Contains(hold!.Render(), output.ToString());
    }

    [Theory]
    [InlineData("Decline this amendment.")]
    [InlineData("Accept and decline this amendment.")]
    [InlineData("Please investigate further.")]
    public void AnswerWording_WithoutTypedDeclineDoesNotAuthorizeReplan(string answer)
    {
        using var f = new Fixture();
        f.Kernel.SubmitHumanInput(f.Request.Id, answer);
        Assert.NotNull(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
        Assert.Equal(WorkTaskStatus.WaitingForHuman, f.Planner.Status);
        Assert.Empty(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
    }

    [Fact]
    public void ReplayedAnsweredAmendment_PreservesHoldAndNeverCompletesOldBatch()
    {
        using var f = new Fixture();
        var verification = f.Planner.LastVerification!;
        f.Kernel.SubmitHumanInput(f.Request.Id, "Accept the proposed text.");
        f.Kernel.RecordDispatchExecutionResult(f.Goal.Id, f.Planner.Id, verification);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, f.Planner.Status);
        Assert.NotNull(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
        Assert.Single(f.Kernel.HumanInputRequests);
        Assert.DoesNotContain(f.Goal.Timeline, item => item.Kind is ProgressKind.TaskCompleted or ProgressKind.TaskRetried);
    }

    [Fact]
    public void RevisionAndTypedDecline_RevisionWins()
    {
        using var f = new Fixture();
        f.Revise();
        f.Kernel.SubmitHumanInput(f.Request.Id, "Decline", amendmentDisposition: PlannerBriefAmendmentDisposition.Decline);
        var retry = Assert.Single(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        Assert.Contains("authoritative brief revised", retry.Message);
        Assert.Contains("current brief v2", retry.Message);
        Assert.DoesNotContain("typed decline recorded", retry.Message);
    }

    [Fact]
    public void OldAmendmentCompletion_AfterRedispatchCannotReviveOldRequest()
    {
        using var f = new Fixture();
        var old = f.Planner.LastVerification!;
        f.Kernel.SubmitHumanInput(f.Request.Id, "Decline", amendmentDisposition: PlannerBriefAmendmentDisposition.Decline);
        f.Kernel.RecordTaskDispatch(f.Goal.Id, f.Planner.Id,
            new TaskDispatchRecord("planner-worker", "planner.exe", f.Root, DateTimeOffset.UtcNow.AddSeconds(1)));
        var dispatch = f.Planner.LastDispatch;
        f.Kernel.RecordDispatchExecutionResult(f.Goal.Id, f.Planner.Id, old);
        Assert.Same(dispatch, f.Planner.LastDispatch);
        Assert.Null(f.Planner.LastVerification);
        Assert.Empty(f.Kernel.GetPendingHumanInput(f.Goal.Id));
        Assert.Single(f.Kernel.HumanInputRequests);
        Assert.Single(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = CreateTempDirectory();
        internal AgentOrchestratorKernel Kernel { get; set; } = new();
        private readonly GoalId goalId;
        private readonly TaskId plannerId;
        private readonly HumanInputRequestId requestId;
        internal Goal Goal => Kernel.GetGoal(goalId);
        internal TaskSpec Planner => Kernel.GetTask(goalId, plannerId);
        internal HumanInputRequest Request => Kernel.HumanInputRequests.Single(request => request.Id == requestId);
        internal int Dispatches { get; private set; }
        internal int Escalations { get; private set; }

        internal Fixture(bool blocker = false)
        {
            var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
            plannerId = planner.Id;
            var goal = Kernel.CreateGoal("Hold an answered amendment until revision or typed decline", [planner,
                new TaskSpec(TaskId.New(), "Implement", AgentRole.Developer)]);
            goalId = goal.Id;
            Kernel.SetGoalRefinedSpec(goal.Id, new RefinedSpec("Preserve behavior pending ruling.", ["Preserve the existing behavior."],
                VerificationClass.TestVerifiable, [], []));
            Kernel.ActivateGoal(goal.Id, AgentCatalog.Default().Agents);
            var stdout = Path.Combine(Root, "planner.out.log");
            var stderr = Path.Combine(Root, "planner.err.log");
            var exit = Path.Combine(Root, "planner.exit.txt");
            var plan = PlannerContractPlanFixture().Replace(PlannerContractAcceptanceMappingBody,
                "1. disposition=planned; plan=obtain a ruling before implementation", StringComparison.Ordinal);
            File.WriteAllText(stdout, plan + Environment.NewLine +
                """
                PLANNER_BRIEF_AMENDMENT: {"criterion_index":1,"defect":"Contradicts the regression.","evidence":"seed.txt:1","proposed_text":"Preserve the existing behavior."}
                """ + Environment.NewLine + WorkerResultBlock("none", "source inspection", "not-run - Planner role", commit: "none",
                    blockers: blocker ? "exact-blocker - brief amendment requested for criterion 1" : "none"));
            File.WriteAllText(stderr, string.Empty);
            File.WriteAllText(exit, "0");
            var now = DateTimeOffset.UtcNow;
            Kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("planner-worker", "planner.exe", Root, now));
            Kernel.RecordTaskProcessStarted(goal.Id, planner.Id,
                new TaskProcessRecord(4242, "planner.exe", Root, stdout, stderr, exit, now, null, null));
            new BackgroundDispatchRunner(isStillRunning: _ => false).RefreshLatestProcess(Kernel, goal.Id, planner.Id);
            Assert.Equal(WorkTaskStatus.WaitingForHuman, planner.Status);
            var request = Assert.Single(Kernel.GetPendingHumanInput(goal.Id));
            Assert.Equal(HumanWaitKind.PlannerBriefAmendment, request.Kind);
            Assert.Equal(1, request.Amendment!.CriterionIndex);
            requestId = request.Id;
        }

        internal void Revise() => Kernel.ReviseGoalBrief(goalId, "Authoritative revision preserves the current regression.");
        internal ConductorAdvanceResult Tick() => ConductorDriverTests.MakeDriver(
            getFacts: _ => new GoalLifecycleFacts(WorkspaceExists: true),
            normalizeLifecycleState: (goal, reason) => Kernel.NormalizeGoalLifecycleState(goal.Id, reason),
            getAmendmentRevisionPending: goal => Kernel.GetPlannerBriefAmendmentRevisionPending(goal.Id),
            dispatchAndStart: _ => { Dispatches++; return DispatchStartOutcome.Started(); },
            writeEscalation: (_, _, _) => Escalations++).AdvanceOnce(Goal, ConductorAutonomyPolicy.Conservative);

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
