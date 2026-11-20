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

    private const string ProposedCriterion = "Preserve the existing behavior.";
    private const string OriginalCriterion = "Allow the existing behavior to change.\n   Keep the supporting check.";
    private const string DeclaredCriteriaObjective = "Implement the current brief.\r\n\r\n## Acceptance criteria\r\n" +
        "1. " + OriginalCriterion + "\r\n\r\n2. Retain the regression tests.\r\n3. Keep the public API stable." +
        "\r\n\r\n## Scope\r\nPreserve the rest of the brief.";

    [Fact]
    public void TypedAccept_AppliesProposedTextAsBriefVersionTwoAndReplansOnce()
    {
        using var f = new Fixture(objective: DeclaredCriteriaObjective);
        const string note = "Accept the Planner's proposed replacement.";
        var originalCriteria = AcceptanceCriteriaParser.ParseDeclared(f.Goal.AuthoritativeBrief.Text);
        Assert.Equal(ProposedCriterion, f.Request.Amendment!.ProposedText);

        f.Kernel.SubmitHumanInput(f.Request.Id, note, amendmentDisposition: PlannerBriefAmendmentDisposition.Accept);

        var brief = f.Goal.AuthoritativeBrief;
        var revisedCriteria = AcceptanceCriteriaParser.ParseDeclared(brief.Text);
        Assert.Equal(2, brief.Version);
        Assert.Equal(ProposedCriterion, revisedCriteria[0]);
        Assert.Equal(originalCriteria.Skip(1), revisedCriteria.Skip(1));
        Assert.Equal(DeclaredCriteriaObjective.Replace(OriginalCriterion, ProposedCriterion, StringComparison.Ordinal), brief.Text);
        Assert.Equal(note, brief.Reason);
        Assert.True(f.Request.IsCompleted);
        Assert.Equal(PlannerBriefAmendmentDisposition.Accept, f.Request.AuthoritativeAnswer!.AmendmentDisposition);
        Assert.Equal(WorkTaskStatus.Assigned, f.Planner.Status);
        Assert.Null(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
        Assert.Single(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
    }

    [Fact]
    public void TypedDecline_SameFixtureLeavesBriefUnchangedAndReplans()
    {
        using var f = new Fixture(objective: DeclaredCriteriaObjective);

        f.Kernel.SubmitHumanInput(f.Request.Id, "Keep the current criterion.",
            amendmentDisposition: PlannerBriefAmendmentDisposition.Decline);

        Assert.Equal(1, f.Goal.AuthoritativeBrief.Version);
        Assert.Equal(DeclaredCriteriaObjective, f.Goal.AuthoritativeBrief.Text);
        Assert.Equal(PlannerBriefAmendmentDisposition.Decline, f.Request.AuthoritativeAnswer!.AmendmentDisposition);
        Assert.Equal(WorkTaskStatus.Assigned, f.Planner.Status);
        Assert.Null(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
        var retry = Assert.Single(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
        Assert.Contains("typed decline recorded", retry.Message);
    }

    [Fact]
    public void TypedAccept_WithoutStoredProposedTextIsRejectedUnanswered()
    {
        using var f = new Fixture(objective: DeclaredCriteriaObjective);
        ReloadWithoutProposedText(f);
        var brief = f.Goal.AuthoritativeBrief;

        var error = Assert.Throws<PlannerBriefAmendmentAcceptRejectedException>(() =>
            f.Kernel.SubmitHumanInput(f.Request.Id, "Apply the proposed criterion.",
                amendmentDisposition: PlannerBriefAmendmentDisposition.Accept));

        Assert.Contains("no stored proposed text", error.Message);
        AssertAmendmentUnanswered(f, brief);
    }

    [Fact]
    public void TypedAccept_CriterionIndexOutsideDeclaredCriteriaIsRejectedUnanswered()
    {
        using var f = new Fixture();
        var brief = f.Goal.AuthoritativeBrief;

        var error = Assert.Throws<PlannerBriefAmendmentAcceptRejectedException>(() =>
            f.Kernel.SubmitHumanInput(f.Request.Id, "Apply the proposed criterion.",
                amendmentDisposition: PlannerBriefAmendmentDisposition.Accept));

        Assert.Contains("outside the brief's 0 declared criteria", error.Message);
        AssertAmendmentUnanswered(f, brief);
    }

    [Fact]
    public void TypedAccept_BriefAlreadyRevisedIsRejectedUnanswered()
    {
        using var f = new Fixture(objective: DeclaredCriteriaObjective);
        f.Kernel.ReviseGoalBrief(f.Goal.Id, "Updated implementation context.\r\n\r\n" + DeclaredCriteriaObjective);
        var brief = f.Goal.AuthoritativeBrief;
        Assert.Equal(2, brief.Version);

        var error = Assert.Throws<PlannerBriefAmendmentAcceptRejectedException>(() =>
            f.Kernel.SubmitHumanInput(f.Request.Id, "Apply the proposal from brief version one.",
                amendmentDisposition: PlannerBriefAmendmentDisposition.Accept));

        Assert.Contains("current brief is v2", error.Message);
        AssertAmendmentUnanswered(f, brief);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task UnsupportedAmendmentValue_IsRejectedByKernelAndCli()
    {
        using var f = new Fixture(objective: DeclaredCriteriaObjective);
        var brief = f.Goal.AuthoritativeBrief;
        const string note = "Apply the proposed criterion.";
        const string rejection = "accepts only accept or decline";
        const PlannerBriefAmendmentDisposition unsupported = (PlannerBriefAmendmentDisposition)99;

        var kernelError = Assert.Throws<ArgumentException>(() =>
            f.Kernel.SubmitHumanInput(f.Request.Id, note, amendmentDisposition: unsupported));
        Assert.Contains(rejection, kernelError.Message);
        AssertAmendmentUnanswered(f, brief);

        var workspace = CreateRefinedWorkspace(f.Root);
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        var cliError = Assert.Throws<ArgumentException>(() =>
            ExecuteAttentionAnswer(f, workspace, ["--amendment", "maybe", note]));
        Assert.Contains(rejection, cliError.Message);
        Assert.Empty(await intents.ListForGoalAsync(f.Goal.Id.Value));
        AssertAmendmentUnanswered(f, brief);

        var payload = new AnswerOperatorIntentPayload(OperatorAnswerTargetKind.HumanInput, f.Request.Id.Value,
            f.Goal.Id.Value, note, OperatorActorKind.Human, AmendmentDisposition: unsupported);
        var id = Guid.NewGuid().ToString("N");
        var queued = await intents.EnqueueAsync(new OperatorIntentRecord(id, id, OperatorIntentVerbs.Answer,
            f.Goal.Id.Value, null, JsonSerializer.Serialize(payload, OperatorIntentJson.Options), [], "operator", "cli",
            "local-process", DateTimeOffset.UtcNow));
        var coordinator = OperatorIntentCoordinator.CreateDefault(workspace);

        Assert.False(coordinator.ExecutePending(f.Kernel, f.Goal).MutatedGoalState);
        var rejected = await intents.GetAsync(queued.Id);
        Assert.Equal(OperatorIntentStatus.Rejected, rejected!.Status);
        Assert.Contains(rejection, rejected.Outcome);
        AssertAmendmentUnanswered(f, brief);
    }

    [Fact(Timeout = TestHangGuard.WholeTestMilliseconds)]
    public async Task AcceptFlag_CliIntentAndCoordinatorApplyBriefVersionTwo()
    {
        using var f = new Fixture(objective: DeclaredCriteriaObjective);
        var originalBrief = f.Goal.AuthoritativeBrief;
        var workspace = CreateRefinedWorkspace(f.Root);
        var intents = SqliteOperatorIntentStore.ForDirectories(workspace.OrchestratorDirectory, workspace.LogDirectory);
        const string note = "Apply the proposed criterion.";
        var notePath = Path.Combine(f.Root, "answer.txt");
        await File.WriteAllTextAsync(notePath, note);

        ExecuteAttentionAnswer(f, workspace, ["--amendment", "accept", "--text-file", notePath]);

        var pending = Assert.Single(await intents.ListForGoalAsync(f.Goal.Id.Value));
        Assert.Equal(OperatorIntentStatus.Pending, pending.Status);
        var payload = JsonSerializer.Deserialize<AnswerOperatorIntentPayload>(pending.PayloadJson, OperatorIntentJson.Options);
        Assert.Equal(PlannerBriefAmendmentDisposition.Accept, payload!.AmendmentDisposition);
        Assert.Equal(note, payload.Text);
        AssertAmendmentUnanswered(f, originalBrief);

        var coordinator = OperatorIntentCoordinator.CreateDefault(workspace);
        Assert.True(coordinator.ExecutePending(f.Kernel, f.Goal).MutatedGoalState);
        coordinator.CompletePersisted([f.Goal.Id]);

        Assert.Equal(OperatorIntentStatus.Applied, (await intents.GetAsync(pending.Id))!.Status);
        Assert.Equal(2, f.Goal.AuthoritativeBrief.Version);
        Assert.Equal(ProposedCriterion, AcceptanceCriteriaParser.ParseDeclared(f.Goal.AuthoritativeBrief.Text)[0]);
        Assert.Equal(WorkTaskStatus.Assigned, f.Planner.Status);
    }

    [Fact]
    public void Snapshot_RoundTripsProposedTextAndLoadsPayloadWithoutIt()
    {
        using var f = new Fixture(objective: DeclaredCriteriaObjective);
        var json = JsonSerializer.Serialize(f.Kernel.ExportSnapshot());

        f.Kernel = AgentOrchestratorKernel.FromSnapshot(JsonSerializer.Deserialize<OrchestratorSnapshot>(json)!);
        Assert.Equal(new PlannerBriefAmendmentPayload(1, 1, ProposedCriterion), f.Request.Amendment);

        ReloadWithoutProposedText(f);
        Assert.Equal(new PlannerBriefAmendmentPayload(1, 1), f.Request.Amendment);
        Assert.False(f.Request.IsCompleted);
    }

    [Fact]
    public void LateAccept_AppliesHeldAnswerThroughSupersede()
    {
        using var f = new Fixture(objective: DeclaredCriteriaObjective);
        f.Kernel.SubmitHumanInput(f.Request.Id, "Apply the proposed criterion.");
        var hold = f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id);
        Assert.NotNull(hold);
        Assert.Contains("--amendment accept", hold.Render());
        const string note = "Confirm the proposed criterion should be applied.";

        f.Kernel.SupersedeHumanInput(f.Goal.Id, f.Request.Id, note, HumanInputAnswerOrigin.Operator,
            PlannerBriefAmendmentDisposition.Accept);

        Assert.Equal(2, f.Goal.AuthoritativeBrief.Version);
        Assert.Equal(note, f.Goal.AuthoritativeBrief.Reason);
        Assert.Equal(ProposedCriterion, AcceptanceCriteriaParser.ParseDeclared(f.Goal.AuthoritativeBrief.Text)[0]);
        Assert.Equal(2, f.Request.AnswerHistory.Count);
        Assert.Equal(PlannerBriefAmendmentDisposition.Accept, f.Request.AuthoritativeAnswer!.AmendmentDisposition);
        Assert.Equal(WorkTaskStatus.Assigned, f.Planner.Status);
        Assert.Null(f.Kernel.GetPlannerBriefAmendmentRevisionPending(f.Goal.Id));
        Assert.Single(f.Goal.Timeline.Where(item => item.Kind == ProgressKind.TaskRetried));
    }

    private static void AssertAmendmentUnanswered(Fixture f, GoalBriefVersion brief)
    {
        Assert.False(f.Request.IsCompleted);
        Assert.Empty(f.Request.AnswerHistory);
        Assert.Equal(brief, f.Goal.AuthoritativeBrief);
        Assert.Equal(WorkTaskStatus.WaitingForHuman, f.Planner.Status);
        Assert.DoesNotContain(f.Goal.Timeline, item => item.Kind == ProgressKind.TaskRetried);
    }

    private static void ReloadWithoutProposedText(Fixture f)
    {
        var snapshot = JsonSerializer.SerializeToNode(f.Kernel.ExportSnapshot())!;
        var request = snapshot[nameof(OrchestratorSnapshot.HumanInputRequests)]!.AsArray().Single(node =>
            node![nameof(HumanInputRequestSnapshot.Id)]!.GetValue<string>() == f.Request.Id.Value);
        var amendment = request![nameof(HumanInputRequestSnapshot.Amendment)]!.AsObject();
        Assert.True(amendment.Remove(nameof(PlannerBriefAmendmentPayload.ProposedText)));
        f.Kernel = AgentOrchestratorKernel.FromSnapshot(snapshot.Deserialize<OrchestratorSnapshot>()!);
    }

    private static void ExecuteAttentionAnswer(Fixture f, OrchestratorWorkspace workspace, string[] answerArgs)
    {
        IReadOnlyList<AgentDefinition> agents = AgentCatalog.Default().Agents;
        var profiles = WorkerProfileCatalog.Default();
        Goal? currentGoal = null;
        CaptureConsole(() => CliCommandDispatcher.ExecuteCommand(
            ["attention", "answer", f.Goal.Id.Value[..8], f.Request.Id.Value[..8], .. answerArgs],
            f.Kernel, workspace, ref agents, new InMemoryModelProviderRegistry([]), ref profiles, ref currentGoal));
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

        internal Fixture(bool blocker = false, string? objective = null)
        {
            var planner = new TaskSpec(TaskId.New(), "Plan the implementation.", AgentRole.Planner);
            plannerId = planner.Id;
            var goal = Kernel.CreateGoal(objective ?? "Hold an answered amendment until revision or typed decline", [planner,
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
