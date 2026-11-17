using System.Text.Json;
using Mcg.AgentOrchestrator.Core;

// Parallel-safe: all snapshots, kernels and timestamps belong to the individual case; no I/O.
public sealed class DispatchSnapshotShapingRoundTripTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    private const string Brief = "Preserve\nthe exact brief text.";
    private const string Candidate = "candidate-shaping";
    private const string Request = """
        PLANNER_EVIDENCE_REQUEST: {"criterion_index":1,"evidence_key":"execution","availability":"post-implementation","owner":"acceptance-gate","needed":"Run regression checks","reason":"Acceptance executes","tests":["DispatchSnapshotShapingRoundTripTests"]}
        """;
    private const string Result = "WORKER_RESULT:\nfiles: none\ncommands: inspect\ntests: not-run - fixture\ncommit: none\nblockers: none\nmodel_fit: fixture/model - adequate\nskills: none\nconfidence: high\nEND_WORKER_RESULT";

    [Fact]
    public void OldAndNewJson_RestoreEqualLastDispatchVerificationAndNewestEvidence()
    {
        var original = CreateGoal();
        var inline = InlineSnapshot(original);
        var shaped = original.ToSnapshot();
        var inlineTask = Assert.Single(inline.Tasks);
        var shapedTask = Assert.Single(shaped.Tasks);
        Assert.All(inlineTask.DispatchHistory!, dispatch =>
        {
            Assert.Equal(Brief, dispatch.BriefSnapshot);
            Assert.Null(dispatch.BriefSnapshotFromVersion);
            Assert.Single(dispatch.ContextPackageReceipt!.Sections);
        });
        Assert.All(shapedTask.DispatchHistory!, dispatch =>
        {
            Assert.Null(dispatch.BriefSnapshot);
            Assert.True(dispatch.BriefSnapshotFromVersion);
        });
        Assert.Empty(shapedTask.DispatchHistory![0].ContextPackageReceipt!.Sections);
        Assert.Equal(1, shapedTask.DispatchHistory[0].ContextPackageReceipt!.SectionCount);
        Assert.Single(shapedTask.DispatchHistory[^1].ContextPackageReceipt!.Sections);

        var oldGoal = Restore(inline);
        var newGoal = Restore(shaped);
        var oldTask = Assert.Single(oldGoal.Tasks);
        var newTask = Assert.Single(newGoal.Tasks);
        AssertJsonEqual(original.Tasks[0].LastDispatch, oldTask.LastDispatch);
        AssertJsonEqual(oldTask.LastDispatch, newTask.LastDispatch);
        AssertJsonEqual(original.Tasks[0].ToSnapshot().LastVerification, oldTask.ToSnapshot().LastVerification);
        AssertJsonEqual(oldTask.LastVerification, newTask.LastVerification);
        Assert.Equal("verification output", newTask.LastVerification!.AuthoritativeStandardOutput);
        Assert.Equal("verification diagnostics", newTask.LastVerification.AuthoritativeStandardError);
        Assert.All(oldTask.DispatchHistory, dispatch => Assert.Equal(Brief, dispatch.BriefSnapshot));
        Assert.All(newTask.DispatchHistory, dispatch => Assert.Equal(Brief, dispatch.BriefSnapshot));
        foreach (var task in new[] { original.Tasks[0], oldTask, newTask })
        {
            Assert.True(task.LastDispatch!.ContextPackageReceipt!.HasEarlyConvergenceEvidenceFor(Candidate));
            Assert.False(task.LastDispatch.ContextPackageReceipt.HasEarlyConvergenceEvidenceFor("other-candidate"));
            Assert.False((task.LastDispatch.ContextPackageReceipt with { Sections = [] })
                .HasEarlyConvergenceEvidenceFor(Candidate));
        }

        // Historical details intentionally stay summarized, including after another save/restore.
        var savedAgain = Restore(newGoal.ToSnapshot());
        Assert.Empty(savedAgain.Tasks[0].DispatchHistory[0].ContextPackageReceipt!.Sections);
        Assert.Equal(1, savedAgain.Tasks[0].DispatchHistory[0].ContextPackageReceipt!.SectionCount);
        AssertJsonEqual(newTask.LastDispatch, savedAgain.Tasks[0].LastDispatch);
        AssertJsonEqual(newTask.LastVerification, savedAgain.Tasks[0].LastVerification);
    }

    [Theory]
    [InlineData("Different inline text")]
    [InlineData("preserve\nthe exact brief text.")]
    [InlineData("Preserve\nthe exact brief text. ")]
    [InlineData("")]
    public void DifferingInlineBrief_SurvivesGoalJsonRoundTrip(string text)
    {
        var goal = CreateGoal(text);
        var stored = Restore(InlineSnapshot(goal)).ToSnapshot();
        Assert.All(stored.Tasks[0].DispatchHistory!, dispatch =>
        {
            Assert.Equal(text, dispatch.BriefSnapshot);
            Assert.Null(dispatch.BriefSnapshotFromVersion);
        });
        Assert.Equal(text, stored.Tasks[0].LastDispatch!.BriefSnapshot);
        var restored = Restore(stored);
        Assert.All(restored.Tasks[0].DispatchHistory, dispatch => Assert.Equal(text, dispatch.BriefSnapshot));
        Assert.Equal(text, restored.Tasks[0].LastDispatch!.BriefSnapshot);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PlannerProvenanceAndObsoleteRound_AgreeAcrossShapes(bool hasBrief)
    {
        var goal = CreateGoal(hasBrief ? Brief : null, AgentRole.Planner);
        var planner = goal.Tasks[0];
        var verification = planner.LastVerification! with
        {
            DispatchAttemptAt = planner.LastDispatch!.DispatchedAt,
            PlannerEvidenceBatch = AgentOutputDirectives.ParseHumanInputRequest(Request, AgentRole.Planner)
        };
        Assert.Single(verification.PlannerEvidenceBatch!.Directives);
        planner.RecordVerification(verification);
        goal.ReviseBrief("The next brief has different text.", "test revision", Start.AddHours(2));

        foreach (var snapshot in new[] { InlineSnapshot(goal), goal.ToSnapshot() })
        {
            var restored = Restore(snapshot);
            var task = Assert.Single(restored.Tasks);
            Assert.Equal(hasBrief ? Brief : null, task.LastDispatch!.BriefSnapshot);
            Assert.Equal(hasBrief, PlannerEvidenceRecorder.IsObsoletePlannerEvidenceRound(restored, task));
            Assert.Null(PlannerEvidenceCompletion.ValidateProvenance(task, task.LastVerification!, out var version));
            Assert.Equal(hasBrief ? 1 : (int?)null, version);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void DeferredReviewReconciliation_AgreesAcrossShapesAndPreservesLegacyNull(bool newShape, bool absentReviewBrief)
    {
        var kernel = CreateDeferredReview();
        var goal = Assert.Single(kernel.Goals);
        var reviewer = goal.Tasks.Single(task => task.RequiredRole == AgentRole.Reviewer);
        Assert.Equal(WorkTaskStatus.Failed, reviewer.Status);
        Assert.Contains(goal.Timeline, item => item.TaskId == reviewer.Id &&
            item.Message.StartsWith("Reviewer WORKER_RESULT criteria attestation rejected:", StringComparison.Ordinal));
        var exported = kernel.ExportSnapshot();
        var snapshot = newShape ? goal.ToSnapshot() : InlineSnapshot(goal);
        if (absentReviewBrief)
        {
            snapshot = snapshot with
            {
                Tasks = snapshot.Tasks.Select(task => task.Id != reviewer.Id.Value ? task : task with
                {
                    LastDispatch = task.LastDispatch! with { BriefSnapshot = null, BriefSnapshotFromVersion = null },
                    DispatchHistory = task.DispatchHistory!.Select(dispatch => dispatch with
                    { BriefSnapshot = null, BriefSnapshotFromVersion = null }).ToArray()
                }).ToArray()
            };
        }
        var restoredKernel = AgentOrchestratorKernel.FromSnapshot(JsonRoundTrip(exported with { Goals = [snapshot] }));
        var restoredGoal = restoredKernel.GetGoal(goal.Id);
        var restoredReviewer = restoredKernel.GetTask(goal.Id, reviewer.Id);
        Assert.Equal(absentReviewBrief ? null : Brief, restoredReviewer.LastDispatch!.BriefSnapshot);
        var verificationBefore = JsonSerializer.Serialize(restoredReviewer.LastVerification);

        Assert.True(restoredKernel.NormalizeGoalLifecycleState(goal.Id, "restore accepted Planner ownership"));

        Assert.Single(restoredGoal.CriterionEvidenceObligations);
        Assert.Equal(absentReviewBrief ? WorkTaskStatus.Failed : WorkTaskStatus.Completed, restoredReviewer.Status);
        Assert.Equal(verificationBefore, JsonSerializer.Serialize(restoredReviewer.LastVerification));
    }

    private static Goal CreateGoal(string? dispatchBrief = Brief, AgentRole role = AgentRole.Developer)
    {
        var hash = new string('a', 64);
        var receipt = new WorkerContextPackageReceipt("ctxpkg-v1-sha256:" + new string('b', 64),
            [new WorkerContextSectionReceipt($"goal/review-finding-receipts/{hash}.json", 800, 900, hash,
                ContextDeliveryMode.OnDemandFile, 1, [role], MandatoryRelativePath: $"receipts/{hash}.json")],
            ProviderUsageValue.Reported(1000), ProviderUsageValue.Reported(200), ProviderUsageValue.Reported(30),
            EarlyConvergenceEligible: true, EarlyConvergenceCandidateSha: Candidate,
            EarlyConvergenceReceiptHashes: [hash]);
        var task = new TaskSpec(new TaskId("round-trip-task"), "Work", role);
        for (var index = 0; index < 2; index++)
            task.RecordDispatch(new TaskDispatchRecord("worker", "command", "root", Start.AddMinutes(index),
                BriefVersion: 1, BriefSnapshot: dispatchBrief, ContextPackageReceipt: receipt,
                ProviderName: "provider", ModelName: "model", BaseCommit: "base", ResultCommit: Candidate));
        task.RecordVerification(new TaskVerificationRecord("verify", "root", 0,
            "verification output", "verification diagnostics", Start.AddHours(1),
            WorkerResultPresent: true, FullStandardOutput: "verification output",
            FullStandardError: "verification diagnostics"));
        return new Goal(new GoalId("round-trip-goal"), Brief, [task]);
    }

    private static AgentOrchestratorKernel CreateDeferredReview()
    {
        var kernel = new AgentOrchestratorKernel();
        var planner = new TaskSpec(new TaskId("planner"), "Plan", AgentRole.Planner);
        var reviewer = new TaskSpec(new TaskId("reviewer"), "Review source safety and execution", AgentRole.Reviewer);
        var goal = kernel.CreateGoal(Brief, [planner, reviewer]);
        kernel.RecordGoalRefinement(goal.Id, new RefinedSpec("Preserve source review and defer execution only",
            ["Preserve source safety and execute the candidate regression checks."], VerificationClass.TestVerifiable, [], []));
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        kernel.RecordTaskDispatch(goal.Id, planner.Id, new TaskDispatchRecord("fixture", "plan", "root", Start));
        kernel.RecordDispatchExecutionResult(goal.Id, planner.Id, new TaskVerificationRecord("plan", "root", 0,
            Request + "\n" + Result, "", Start.AddMinutes(1), WorkerResultPresent: true, DispatchAttemptAt: Start));
        Assert.Equal(WorkTaskStatus.Completed, planner.Status);
        Assert.Single(goal.CriterionEvidenceObligations);

        // Reproduce a persisted accepted Planner round whose ownership projection needs repair.
        var exported = kernel.ExportSnapshot();
        kernel = AgentOrchestratorKernel.FromSnapshot(exported with
        { Goals = [exported.Goals[0] with { CriterionEvidenceObligations = [] }] });
        kernel.RecordTaskDispatch(goal.Id, reviewer.Id, new TaskDispatchRecord("fixture", "review", "root", Start.AddMinutes(2)));
        kernel.RecordDispatchBaseCommit(goal.Id, reviewer.Id, Candidate);
        var review = Result.Replace("END_WORKER_RESULT", """
            findings: []
            touched_anchors: []
            criteria_verdicts: [{"criterion_index":0,"verdict":"not-verifiable","evidence":"Source inspected; Acceptance executes regression checks"}]
            verdict: pass
            END_WORKER_RESULT
            """, StringComparison.Ordinal);
        kernel.RecordDispatchExecutionResult(goal.Id, reviewer.Id, new TaskVerificationRecord("review", "root", 0,
            review, "", Start.AddMinutes(3), WorkerResultPresent: true, ReviewedCommit: Candidate,
            DispatchAttemptAt: Start.AddMinutes(2)));
        return kernel;
    }

    private static GoalSnapshot InlineSnapshot(Goal goal) =>
        goal.ToSnapshot() with { Tasks = goal.Tasks.Select(task => task.ToSnapshot()).ToArray() };

    private static Goal Restore(GoalSnapshot snapshot) => Goal.FromSnapshot(JsonRoundTrip(snapshot));

    private static T JsonRoundTrip<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value))!;

    private static void AssertJsonEqual<T>(T expected, T actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected), JsonSerializer.Serialize(actual));
}
