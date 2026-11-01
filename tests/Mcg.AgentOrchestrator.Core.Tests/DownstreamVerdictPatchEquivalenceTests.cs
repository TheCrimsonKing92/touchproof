using Mcg.AgentOrchestrator.Core;

// Parallel safe: each case owns its kernel and collaborator; no I/O or shared state.
public sealed class DownstreamVerdictPatchEquivalenceTests
{
    private static readonly string CandidateA = new('a', 40);
    private static readonly string CandidateB = new('b', 40);
    private static readonly DateTimeOffset RoundAt = DateTimeOffset.Parse("2026-11-01T12:00:00Z");

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester, false)]
    [Xunit.InlineData(AgentRole.Reviewer, false)]
    [Xunit.InlineData(AgentRole.Tester, true)]
    [Xunit.InlineData(AgentRole.Reviewer, true)]
    public void EquivalentCandidate_PreservesVerdictAndPersistsCoverage(AgentRole role, bool retryPath)
    {
        var (kernel, goal, developer, downstream) = CreateCompletedVerdict(role, retryPath);
        var equivalence = new RecordingEquivalence("equivalent");
        kernel.SetCandidatePatchEquivalence(equivalence);
        var prior = downstream.LastVerification!;

        CarryOrInvalidate(kernel, goal, developer, retryPath);

        Assert.Equal(WorkTaskStatus.Completed, downstream.Status);
        Assert.Equal(prior with { ReviewedCommit = CandidateB }, downstream.LastVerification);
        Assert.Same(downstream.LastVerification, Assert.Single(downstream.VerificationHistory));
        Assert.Equal((goal.Id, CandidateA, CandidateB), Assert.Single(equivalence.Calls));
        Assert.Contains(goal.Timeline, item => item.TaskId == downstream.Id &&
            item.Kind == ProgressKind.TaskUpdated &&
            item.Message == $"Preserved {role} task because candidate {CandidateB} is patch-equivalent to reviewed candidate {CandidateA} (ev).");
        Assert.DoesNotContain(goal.Timeline, item => item.TaskId == downstream.Id &&
            item.Kind == ProgressKind.TaskRetried && item.Message.StartsWith("Invalidated ", StringComparison.Ordinal));

        var restored = AgentOrchestratorKernel.FromSnapshot(kernel.ExportSnapshot());
        var restoredTask = restored.Goals.Single().Tasks.Single(task => task.Id == downstream.Id);
        Assert.Equal(CandidateB, restoredTask.LastVerification!.ReviewedCommit);
        Assert.Same(restoredTask.LastVerification, Assert.Single(restoredTask.VerificationHistory));

        if (retryPath) CompleteDispatch(kernel, goal, developer, CandidateB, CandidateB);
        kernel.RetryTask(goal.Id, developer.Id, "Repeat on covered candidate.");
        CompleteDispatch(kernel, goal, developer, CandidateB, CandidateB);
        Assert.Single(equivalence.Calls);
        Assert.Equal(WorkTaskStatus.Completed, downstream.Status);
        Assert.Contains(goal.Timeline, item => item.TaskId == downstream.Id &&
            item.Message.Contains($"verification covers candidate {CandidateB}", StringComparison.Ordinal));

        kernel.RetryTask(goal.Id, downstream.Id, "Replace retained verdict.");
        Assert.Null(downstream.LastVerification);
    }

    [Xunit.Theory]
    [Xunit.InlineData("different", false)]
    [Xunit.InlineData("throws", false)]
    [Xunit.InlineData("absent", false)]
    [Xunit.InlineData("empty-evidence", false)]
    [Xunit.InlineData("different", true)]
    [Xunit.InlineData("throws", true)]
    [Xunit.InlineData("absent", true)]
    [Xunit.InlineData("empty-evidence", true)]
    public void UnprovenEquivalence_InvalidatesWithExistingMessage(string outcome, bool retryPath)
    {
        var (kernel, goal, developer, tester) = CreateCompletedVerdict(AgentRole.Tester, retryPath);
        var equivalence = new RecordingEquivalence(outcome);
        if (outcome != "absent") kernel.SetCandidatePatchEquivalence(equivalence);

        CarryOrInvalidate(kernel, goal, developer, retryPath);

        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Null(tester.LastVerification);
        Assert.Equal(CandidateA, Assert.Single(tester.VerificationHistory).ReviewedCommit);
        var expected = retryPath
            ? $"Invalidated Tester task because upstream Developer task {developer.Id.Value[..8]} was retried."
            : $"Invalidated Tester task because retried upstream Developer task {developer.Id.Value[..8]} changed candidate from {CandidateA} to {CandidateB}.";
        Assert.Contains(goal.Timeline, item => item.TaskId == tester.Id &&
            item.Kind == ProgressKind.TaskRetried && item.Message == expected);
        Assert.DoesNotContain(goal.Timeline, item => item.TaskId == tester.Id &&
            item.Message.Contains("patch-equivalent", StringComparison.Ordinal));
        Assert.Equal(outcome == "absent" ? 0 : 1, equivalence.Calls.Count);
    }

    [Xunit.Fact]
    public void FailedTester_RetryResetsWithoutCallingEquivalence()
    {
        var (kernel, goal, developer, tester) = CreateCompletedVerdict(
            AgentRole.Tester, retryPath: true, succeeded: false);
        var equivalence = new RecordingEquivalence("equivalent");
        kernel.SetCandidatePatchEquivalence(equivalence);
        Assert.Equal(WorkTaskStatus.Failed, tester.Status);

        kernel.RetryTask(goal.Id, developer.Id, "Revise failed candidate.");

        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Null(tester.LastVerification);
        Assert.Empty(equivalence.Calls);
        Assert.Contains(goal.Timeline, item => item.TaskId == tester.Id &&
            item.Kind == ProgressKind.TaskRetried &&
            item.Message == $"Invalidated Tester task because upstream Developer task {developer.Id.Value[..8]} was retried.");
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Tester)]
    [Xunit.InlineData(AgentRole.Reviewer)]
    public void ExactSha_PreservesWithoutCallingEquivalence(AgentRole role)
    {
        var (kernel, goal, developer, downstream) = CreateCompletedVerdict(role, retryPath: false);
        var equivalence = new RecordingEquivalence("throws");
        kernel.SetCandidatePatchEquivalence(equivalence);
        var prior = downstream.LastVerification;

        kernel.RetryTask(goal.Id, developer.Id, "Repeat unchanged candidate.");
        CompleteDispatch(kernel, goal, developer, CandidateA, CandidateA);

        Assert.Equal(WorkTaskStatus.Completed, downstream.Status);
        Assert.Same(prior, downstream.LastVerification);
        Assert.Empty(equivalence.Calls);
        Assert.DoesNotContain(goal.Timeline, item => item.TaskId == downstream.Id &&
            item.Kind == ProgressKind.TaskRetried);
        Assert.Contains(goal.Timeline, item => item.TaskId == downstream.Id &&
            item.Message.Contains($"completed with unchanged candidate {CandidateA}", StringComparison.Ordinal));
    }

    [Xunit.Fact]
    public void MissingReviewedCommit_InvalidatesWithoutCallingEquivalence()
    {
        var (kernel, goal, developer, tester) = CreateCompletedVerdict(
            AgentRole.Tester, retryPath: false, reviewedCommit: " ");
        var equivalence = new RecordingEquivalence("equivalent");
        kernel.SetCandidatePatchEquivalence(equivalence);

        CarryOrInvalidate(kernel, goal, developer, retryPath: false);

        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Null(tester.LastVerification);
        Assert.Empty(equivalence.Calls);
    }

    [Xunit.Theory]
    [Xunit.InlineData(AgentRole.Developer, WorkTaskStatus.Completed)]
    [Xunit.InlineData(AgentRole.Planner, WorkTaskStatus.Completed)]
    [Xunit.InlineData(AgentRole.Researcher, WorkTaskStatus.Completed)]
    [Xunit.InlineData(AgentRole.Tester, WorkTaskStatus.Pending)]
    [Xunit.InlineData(AgentRole.Tester, WorkTaskStatus.Assigned)]
    [Xunit.InlineData(AgentRole.Tester, WorkTaskStatus.Running)]
    [Xunit.InlineData(AgentRole.Tester, WorkTaskStatus.WaitingForHuman)]
    [Xunit.InlineData(AgentRole.Tester, WorkTaskStatus.Failed)]
    [Xunit.InlineData(AgentRole.Reviewer, WorkTaskStatus.Cancelled)]
    public void IneligibleVerdict_DoesNotConsultSeam(AgentRole role, WorkTaskStatus status)
    {
        var kernel = new AgentOrchestratorKernel();
        var downstream = new TaskSpec(TaskId.New(), "Ineligible verdict", role);
        var goal = kernel.CreateGoal("Eligibility guard", [downstream]);
        downstream.RecordVerification(new TaskVerificationRecord(
            "verify", "C:\\repo", 0, "passed", string.Empty, RoundAt, ReviewedCommit: CandidateA));
        downstream.SetStatus(status);
        var prior = downstream.LastVerification;
        var equivalence = new RecordingEquivalence("equivalent");

        Assert.False(DownstreamVerdictPatchEquivalence.TryCarry(
            equivalence, goal, downstream, CandidateB, out _, out _));

        Assert.Empty(equivalence.Calls);
        Assert.Same(prior, downstream.LastVerification);
        Assert.Equal(status, downstream.Status);
    }

    [Xunit.Fact]
    public void UnknownCurrentCandidate_InvalidatesWithoutCallingEquivalence()
    {
        var (kernel, goal, developer, tester) = CreateCompletedVerdict(AgentRole.Tester, retryPath: false);
        var equivalence = new RecordingEquivalence("equivalent");
        kernel.SetCandidatePatchEquivalence(equivalence);
        kernel.RetryTask(goal.Id, developer.Id, "Close without candidate proof.");

        kernel.ReportTaskProgress(goal.Id, developer.Id, WorkTaskStatus.Completed, "Closed without proof.");

        Assert.Equal(WorkTaskStatus.Assigned, tester.Status);
        Assert.Null(tester.LastVerification);
        Assert.Empty(equivalence.Calls);
        Assert.Contains(goal.Timeline, item => item.TaskId == tester.Id &&
            item.Kind == ProgressKind.TaskRetried &&
            item.Message.Contains($"did not prove candidate {CandidateA} unchanged", StringComparison.Ordinal));
    }

    private static (AgentOrchestratorKernel Kernel, Goal Goal, TaskSpec Developer, TaskSpec Downstream)
        CreateCompletedVerdict(AgentRole role, bool retryPath, bool succeeded = true, string? reviewedCommit = null)
    {
        var kernel = new AgentOrchestratorKernel();
        var developer = new TaskSpec(TaskId.New(), "Implement candidate", AgentRole.Developer);
        var downstream = new TaskSpec(TaskId.New(), "Verify candidate", role);
        var goal = kernel.CreateGoal("Carry completed verdict coverage", [developer, downstream]);
        kernel.ActivateGoal(goal.Id, DefaultAgents());
        CompleteDispatch(kernel, goal, developer, retryPath ? CandidateB : CandidateA, CandidateA);
        CompleteDispatch(kernel, goal, downstream, CandidateA, reviewedCommit ?? CandidateA, succeeded);
        return (kernel, goal, developer, downstream);
    }

    private static void CarryOrInvalidate(AgentOrchestratorKernel kernel, Goal goal, TaskSpec developer, bool retryPath)
    {
        kernel.RetryTask(goal.Id, developer.Id, "Retry developer candidate.");
        if (!retryPath) CompleteDispatch(kernel, goal, developer, CandidateB, CandidateB);
    }

    private static void CompleteDispatch(
        AgentOrchestratorKernel kernel, Goal goal, TaskSpec task,
        string resultCommit, string reviewedCommit, bool succeeded = true)
    {
        kernel.RecordTaskDispatch(goal.Id, task.Id,
            new TaskDispatchRecord(task.RequiredRole.ToString(), "worker", "C:\\repo", RoundAt));
        if (!string.IsNullOrWhiteSpace(reviewedCommit))
            kernel.RecordDispatchBaseCommit(goal.Id, task.Id, reviewedCommit);
        kernel.RecordDispatchResultCommit(goal.Id, task.Id, resultCommit);
        kernel.ReportTaskProgress(goal.Id, task.Id,
            succeeded ? WorkTaskStatus.Completed : WorkTaskStatus.Failed, "Round finished.");
        kernel.RecordTaskVerification(goal.Id, task.Id, new TaskVerificationRecord(
            "verify", "C:\\repo", succeeded ? 0 : 1, "round evidence", string.Empty, RoundAt,
            ReviewedCommit: reviewedCommit));
    }

    private sealed class RecordingEquivalence(string outcome) : ICandidatePatchEquivalence
    {
        internal List<(GoalId Goal, string Reviewed, string Current)> Calls { get; } = [];

        public bool AreEquivalent(GoalId goal, string reviewedSha, string currentSha, out string evidence)
        {
            Calls.Add((goal, reviewedSha, currentSha));
            if (outcome == "throws") throw new InvalidOperationException("Git unavailable.");
            evidence = outcome == "empty-evidence" ? string.Empty : "ev";
            return outcome is "equivalent" or "empty-evidence";
        }
    }
}
